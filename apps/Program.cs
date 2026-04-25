using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PrepareForTesting;

internal static class Program
{
    // Process names to terminate before testing. Matched case-insensitively
    // and without the .exe extension (Process.GetProcessesByName convention).
    private static readonly string[] TargetProcesses =
    {
        "teams", "chrome", "msedge", "winword", "excel",
        "OUTLOOK", "ONENOTE", "acrobat", "acrord32",
        "snippingtool", "msteams", "ms-teams", "MSTeamsSetup",
        "TeamsMeetingAddin", "TeamsWebView", "Update",
        "POWERPNT", "Photos", "ONENOTEM", "Notepad", "mspaint",
        "MSACCESS", "CalculatorApp"
    };

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var summary = ProcessKiller.TerminateAll(TargetProcesses);

        ShowResult(summary);
    }

    private static void ShowResult(KillSummary summary)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        string title = $"Testing Preparation v{version.Major}.{version.Minor}.{version.Build}";

        string body;
        MessageBoxIcon icon;

        if (summary.Failed == 0)
        {
            body = "Applications closed successfully.\n\n" +
                   "You're ready to begin testing.\n\n" +
                   $"({summary.Killed} process{(summary.Killed == 1 ? "" : "es")} terminated)";
            icon = MessageBoxIcon.Information;
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("Testing preparation completed with warnings.");
            sb.AppendLine();
            sb.AppendLine($"Terminated: {summary.Killed}");
            sb.AppendLine($"Failed:     {summary.Failed}");

            if (summary.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Issues:");
                foreach (string err in summary.Errors)
                {
                    sb.AppendLine($"  \u2022 {err}");
                }
            }

            body = sb.ToString();
            icon = MessageBoxIcon.Warning;
        }

        MessageBox.Show(body, title, MessageBoxButtons.OK, icon);
    }
}

/// <summary>
/// Results from a termination run.
/// </summary>
internal sealed class KillSummary
{
    public int Killed { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Terminates target processes along with their full descendant tree.
/// </summary>
internal static class ProcessKiller
{
    private const int ProcessExitTimeoutMs = 3000;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 500;

    public static KillSummary TerminateAll(IEnumerable<string> processNames)
    {
        var summary = new KillSummary();

        int selfPid;
        try
        {
            selfPid = Environment.ProcessId;
        }
        catch
        {
            selfPid = -1;
        }

        foreach (string name in processNames)
        {
            TryKill(name, selfPid, summary);
        }

        return summary;
    }

    private static void TryKill(string processName, int selfPid, KillSummary summary)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            Process[] running;
            try
            {
                running = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"Could not enumerate {processName}: {ex.Message}");
                return;
            }

            if (running.Length == 0)
            {
                // Nothing left to kill for this name. Done.
                return;
            }

            bool isFinalAttempt = attempt == MaxRetries;

            foreach (Process proc in running)
            {
                try
                {
                    // Don't kill ourselves.
                    if (proc.Id == selfPid)
                    {
                        continue;
                    }

                    if (proc.HasExited)
                    {
                        continue;
                    }

                    // Kill the process and ALL of its descendants. This catches
                    // Teams' helper processes, Edge renderers, Office sub-processes,
                    // crash handlers, etc.
                    proc.Kill(entireProcessTree: true);

                    if (proc.WaitForExit(ProcessExitTimeoutMs))
                    {
                        summary.Killed++;
                    }
                    else if (isFinalAttempt)
                    {
                        summary.Failed++;
                        summary.Errors.Add(
                            $"{processName} (PID {proc.Id}) did not exit within {ProcessExitTimeoutMs}ms");
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and Kill — fine.
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    // Access denied — protected/elevated process. Only report on final attempt.
                    if (isFinalAttempt)
                    {
                        summary.Failed++;
                        summary.Errors.Add($"Access denied for {processName} (PID {proc.Id}). " +
                                           "Try running as administrator.");
                    }
                }
                catch (Win32Exception ex)
                {
                    if (isFinalAttempt)
                    {
                        summary.Failed++;
                        summary.Errors.Add($"Could not kill {processName} (PID {proc.Id}): {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    if (isFinalAttempt)
                    {
                        summary.Failed++;
                        summary.Errors.Add($"Unexpected error killing {processName}: {ex.Message}");
                    }
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* ignored */ }
                }
            }

            // Brief pause before re-checking. Some apps respawn helpers briefly
            // after the parent dies; retry catches those stragglers.
            if (!isFinalAttempt)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}
