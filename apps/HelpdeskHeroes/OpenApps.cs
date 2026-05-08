using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HelpdeskHeroes;

/// <summary>
/// Best-effort list of "apps the user has open right now."
/// Powers the Desktop App page's pick-list and gets auto-attached to the
/// ticket so the helpdesk can see context (e.g. Word + Teams + a curriculum
/// site open at the same time).
///
/// Mirrors SystemInfo / BrowserTabs defensive style: every step swallows
/// its own exceptions, the whole pass is bounded by a hard timeout, and
/// the worst case is an empty list — never a crash.
/// </summary>
internal static class OpenApps
{
    /// <summary>One detected running app.</summary>
    /// <param name="DisplayName">Friendly name (e.g. "Word").</param>
    /// <param name="ProcessName">Bare exe name without ".exe" (e.g. "WINWORD").</param>
    /// <param name="ProcessId">PID — used by the frozen-app force-close action.</param>
    /// <param name="IsResponding">False when Windows reports the main window
    /// thread is hung. Drives the "(not responding)" hint and the offer to
    /// force-close on the Desktop App page.</param>
    internal sealed record App(
        string DisplayName,
        string ProcessName,
        int ProcessId,
        bool IsResponding);

    private const int CollectTimeoutMs = 2000;

    /// <summary>
    /// Common Windows / shell processes that always have a top-level window
    /// but aren't useful "apps" from a user's perspective. Filtered out
    /// of the picker and the email.
    /// </summary>
    private static readonly HashSet<string> Boring = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ApplicationFrameHost",
        "ShellExperienceHost",
        "SearchHost",
        "SearchApp",
        "SearchUI",
        "StartMenuExperienceHost",
        "TextInputHost",
        "LockApp",
        "SystemSettings",
        "WidgetService",
        "Widgets",
        "Video.UI",
        "RuntimeBroker",
        "SecurityHealthSystray",
        "HelpdeskHeroes", // ourselves, just in case
    };

    /// <summary>
    /// Map well-known executable names (which are often cryptic — WINWORD,
    /// POWERPNT, etc.) to the names users actually call them. Falls back
    /// to FileDescription / ProductName, and finally the process name.
    /// </summary>
    private static readonly Dictionary<string, string> KnownApps =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["OUTLOOK"]      = "Outlook",
        ["WINWORD"]      = "Word",
        ["EXCEL"]        = "Excel",
        ["POWERPNT"]     = "PowerPoint",
        ["MSACCESS"]     = "Access",
        ["ONENOTE"]      = "OneNote",
        ["WINPROJ"]      = "Project",
        ["VISIO"]        = "Visio",
        ["MSPUB"]        = "Publisher",
        ["OneDrive"]     = "OneDrive",
        ["Teams"]        = "Teams",
        ["ms-teams"]     = "Teams",
        ["chrome"]       = "Chrome",
        ["msedge"]       = "Edge",
        ["firefox"]      = "Firefox",
        ["Code"]         = "VS Code",
        ["AcroRd32"]     = "Adobe Reader",
        ["Acrobat"]      = "Adobe Acrobat",
        ["Zoom"]         = "Zoom",
        ["Spotify"]      = "Spotify",
        ["notepad"]      = "Notepad",
        ["mstsc"]        = "Remote Desktop",
        ["SnippingTool"] = "Snipping Tool",
        ["ScreenSketch"] = "Snipping Tool",
        ["Calculator"]   = "Calculator",
        // Common user-fleet titles
        ["Minecraft"]    = "Minecraft",
        ["Roblox"]       = "Roblox",
        ["Discord"]      = "Discord",
        ["Scratch"]      = "Scratch",
    };

    /// <summary>
    /// Enumerate every process that owns at least one visible top-level
    /// window. Deduplicated by process name and sorted alphabetically by
    /// display name. Returns an empty list (never throws) on any failure.
    /// </summary>
    public static List<App> Collect()
    {
        var seen = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int deadline = Environment.TickCount + CollectTimeoutMs;
            int myPid    = Environment.ProcessId;

            foreach (var p in Process.GetProcesses())
            {
                if (TimedOut(deadline)) break;

                try
                {
                    if (p.Id == myPid) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;

                    string procName = p.ProcessName;
                    if (Boring.Contains(procName)) continue;
                    if (seen.ContainsKey(procName)) continue;

                    bool responding = true;
                    try { responding = p.Responding; }
                    catch { /* default to "responding" on access errors */ }

                    seen[procName] = new App(
                        FriendlyName(p, procName),
                        procName,
                        p.Id,
                        responding);
                }
                catch
                {
                    // Process may have exited mid-enumeration, or we may not
                    // have rights to read its module info. Skip and continue.
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // Total failure — return whatever we managed to collect.
        }

        return seen.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Public lookup into the curated process-name → friendly-name map.
    /// Used by <see cref="ActiveContext"/> to label the foreground window
    /// without round-tripping through Collect().
    /// </summary>
    public static bool TryGetFriendlyName(string processName, out string? friendly)
    {
        if (!string.IsNullOrEmpty(processName)
            && KnownApps.TryGetValue(processName, out var hit))
        {
            friendly = hit;
            return true;
        }
        friendly = null;
        return false;
    }

    /// <summary>
    /// Best-effort force-close of a process by PID with a graceful close
    /// attempted first. Returns true if the process was already gone or
    /// successfully terminated. Used by the Desktop App page's "force close"
    /// button when an app is reported as not responding.
    /// </summary>
    public static bool TryForceClose(int pid, int graceMs = 1500)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;

            // Try a clean close first — gives Word etc. a chance to save state.
            try { p.CloseMainWindow(); } catch { /* ignore */ }
            if (p.WaitForExit(graceMs)) return true;

            // Still alive → kill the whole process tree (covers crash dialogs).
            try { p.Kill(entireProcessTree: true); } catch { return false; }
            return p.WaitForExit(graceMs);
        }
        catch (ArgumentException)
        {
            // Process already gone — that's fine.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort relaunch by friendly process name. Uses ShellExecute so
    /// the Start menu / app-paths registry can resolve "WINWORD" → winword.exe.
    /// </summary>
    public static bool TryRelaunch(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = processName, // ShellExecute resolves bare names against App Paths
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FriendlyName(Process p, string fallback)
    {
        // 1. Hand-curated map first (best for the Office suite).
        if (KnownApps.TryGetValue(fallback, out var known)) return known;

        // 2. Executable's FileDescription (set by most app vendors).
        try
        {
            var fvi = p.MainModule?.FileVersionInfo;
            if (fvi != null)
            {
                if (!string.IsNullOrWhiteSpace(fvi.FileDescription)) return fvi.FileDescription!;
                if (!string.IsNullOrWhiteSpace(fvi.ProductName))     return fvi.ProductName!;
            }
        }
        catch
        {
            // Reading MainModule on cross-arch processes can throw Win32Exception.
            // No problem — fall through to the process name.
        }

        // 3. Last resort: capitalize the process name.
        return string.IsNullOrEmpty(fallback)
            ? "(unknown)"
            : char.ToUpperInvariant(fallback[0]) + fallback[1..];
    }

    private static bool TimedOut(int deadlineTicks)
    {
        return unchecked(Environment.TickCount - deadlineTicks) > 0;
    }
}
