using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PrepareForTesting;

internal static class Program
{
    // Target speaker volume (0.0 = silent, 1.0 = max)
    private const float SpeakerVolumeTarget = 0.60f;

    // Process names to terminate before testing. Matched case-insensitively
    // and without the .exe extension (Process.GetProcessesByName convention).
    private static readonly string[] TargetProcesses =
    {
        "teams", "chrome", "msedge", "winword", "excel",
        "OUTLOOK", "olk", "ONENOTE", "acrobat", "acrord32",
        "snippingtool", "msteams", "ms-teams", "MSTeamsSetup", "MSTeamsSetupx64",
        "TeamsMeetingAddin", "TeamsWebView", "Update", "Compass Cloud",
        "POWERPNT", "Photos", "ONENOTEM", "Notepad", "mspaint",
        "MSACCESS", "CalculatorApp"
    };

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var summary = new PrepSummary();
        ProcessKiller.TerminateAll(TargetProcesses, summary);
        AudioControl.SetDefaultPlaybackVolume(SpeakerVolumeTarget, summary);

        ShowResult(summary);
    }

    private static void ShowResult(PrepSummary summary)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        string title = $"Testing Preparation v{version.Major}.{version.Minor}.{version.Build}";

        bool allOk = summary.ProcessesFailed == 0 && summary.AudioConfigured;
        var sb = new StringBuilder();

        if (allOk)
        {
            sb.AppendLine($"Applications closed. Speaker set to {summary.AudioVolumePercent}%.");
            sb.AppendLine();
            sb.AppendLine("You're ready to begin testing.");
        }
        else
        {
            sb.AppendLine("Testing preparation completed with warnings.");
            sb.AppendLine();
            sb.AppendLine($"Applications terminated: {summary.ProcessesKilled}");
            if (summary.ProcessesFailed > 0)
            {
                sb.AppendLine($"Applications failed:     {summary.ProcessesFailed}");
            }
            sb.AppendLine($"Speaker volume:          " +
                          (summary.AudioConfigured
                              ? $"set to {summary.AudioVolumePercent}%"
                              : "NOT configured (please check manually)"));

            if (summary.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Issues:");
                foreach (string err in summary.Errors)
                {
                    sb.AppendLine($"  \u2022 {err}");
                }
            }
        }

        var icon = allOk ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
        MessageBox.Show(sb.ToString(), title, MessageBoxButtons.OK, icon);
    }
}

/// <summary>
/// Outcome of a single preparation run.
/// </summary>
internal sealed class PrepSummary
{
    public int ProcessesKilled { get; set; }
    public int ProcessesFailed { get; set; }
    public bool AudioConfigured { get; set; }
    public int AudioVolumePercent { get; set; }
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

    public static void TerminateAll(IEnumerable<string> processNames, PrepSummary summary)
    {
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
    }

    private static void TryKill(string processName, int selfPid, PrepSummary summary)
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
                return; // Nothing left to kill for this name.
            }

            bool isFinalAttempt = attempt == MaxRetries;

            foreach (Process proc in running)
            {
                try
                {
                    if (proc.Id == selfPid) continue;   // Don't kill ourselves
                    if (proc.HasExited) continue;

                    // Kill the process and ALL of its descendants. This catches
                    // Teams' helper processes, Edge renderers, Office sub-processes,
                    // crash handlers, etc.
                    proc.Kill(entireProcessTree: true);

                    if (proc.WaitForExit(ProcessExitTimeoutMs))
                    {
                        summary.ProcessesKilled++;
                    }
                    else if (isFinalAttempt)
                    {
                        summary.ProcessesFailed++;
                        summary.Errors.Add(
                            $"{processName} (PID {proc.Id}) did not exit within {ProcessExitTimeoutMs}ms");
                    }
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and kill — fine.
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    if (isFinalAttempt)
                    {
                        summary.ProcessesFailed++;
                        summary.Errors.Add($"Access denied for {processName} (PID {proc.Id}). " +
                                           "Try running as administrator.");
                    }
                }
                catch (Win32Exception ex)
                {
                    if (isFinalAttempt)
                    {
                        summary.ProcessesFailed++;
                        summary.Errors.Add($"Could not kill {processName} (PID {proc.Id}): {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    if (isFinalAttempt)
                    {
                        summary.ProcessesFailed++;
                        summary.Errors.Add($"Unexpected error killing {processName}: {ex.Message}");
                    }
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* ignored */ }
                }
            }

            if (!isFinalAttempt)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}

/// <summary>
/// Unmutes the default playback device and sets master volume via the Windows
/// Core Audio API. Implemented with raw COM interop (no NuGet dependencies).
/// </summary>
internal static class AudioControl
{
    public static void SetDefaultPlaybackVolume(float volumeScalar, PrepSummary summary)
    {
        volumeScalar = Math.Clamp(volumeScalar, 0f, 1f);

        object? enumerator = null;
        object? device = null;
        object? endpointVolume = null;

        try
        {
            enumerator = new MMDeviceEnumeratorComObject();
            var enumIface = (IMMDeviceEnumerator)enumerator;

            int hr = enumIface.GetDefaultAudioEndpoint(
                EDataFlow.eRender, ERole.eMultimedia, out IMMDevice deviceIface);
            Marshal.ThrowExceptionForHR(hr);
            device = deviceIface;

            // CLSCTX_ALL = 0x17
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            hr = deviceIface.Activate(ref iid, 0x17, IntPtr.Zero, out object endpointObj);
            Marshal.ThrowExceptionForHR(hr);
            endpointVolume = endpointObj;

            var volumeIface = (IAudioEndpointVolume)endpointObj;
            Guid context = Guid.Empty;

            Marshal.ThrowExceptionForHR(volumeIface.SetMute(false, ref context));
            Marshal.ThrowExceptionForHR(volumeIface.SetMasterVolumeLevelScalar(volumeScalar, ref context));

            summary.AudioConfigured = true;
            summary.AudioVolumePercent = (int)Math.Round(volumeScalar * 100);
        }
        catch (Exception ex)
        {
            summary.AudioConfigured = false;
            summary.Errors.Add($"Could not configure speaker: {ex.Message}");
        }
        finally
        {
            if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }
}

#region Windows Core Audio COM interop

internal enum EDataFlow { eRender, eCapture, eAll }
internal enum ERole { eConsole, eMultimedia, eCommunications }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
    [PreserveSig] int GetChannelCount(out uint pnChannelCount);
    [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
    [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
    [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
    [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

#endregion
