using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace HelpdeskHeroes;

/// <summary>
/// Best-effort device fingerprint that gets attached to outgoing helpdesk
/// tickets so the helpdesk can identify the device without making the
/// user hunt for asset tags / Wi-Fi names / etc.
///
/// Rules:
///  * Every getter is wrapped in <see cref="Safe"/> — any failure becomes
///    "(unknown)" and never throws. The app must keep working even if WMI
///    is locked down, the device is offline, etc.
///  * No personally identifying browser content here — the rich browser-tab
///    capture lives in <see cref="BrowserTabs"/> and is rendered separately
///    in the email body so it stays distinct from the bare device fingerprint.
/// </summary>
internal static class SystemInfo
{
    // -------- Granular getters (used by TroubleshootingReport) --------

    public static string ComputerName  => Safe(() => Environment.MachineName);
    public static string UserName      => Safe(() => Environment.UserName);
    public static string UserDomain    => Safe(() => Environment.UserDomainName);
    public static string SerialNumber  => Safe(GetBiosSerial);
    public static string OsDescription => Safe(GetOsDescription);
    public static string WifiSsid      => Safe(GetWifiSsid);
    public static string IPv4Address   => Safe(GetLocalIPv4);

    public static string Timestamp
    {
        get
        {
            try { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"); }
            catch { return "(unknown)"; }
        }
    }

    public static string AppVersion
    {
        get
        {
            try
            {
                var asm  = Assembly.GetExecutingAssembly();
                var name = asm.GetName();
                return name.Version?.ToString() ?? "(unknown)";
            }
            catch { return "(unknown)"; }
        }
    }

    // -------- Bulk render --------

    /// <summary>
    /// Returns a multi-line string suitable for appending to a plain-text
    /// email body. Always ends with a trailing newline. Used as the
    /// "device fingerprint" section of the auto-generated helpdesk ticket;
    /// the report builder calls this directly so the formatting stays in
    /// one place.
    /// </summary>
    public static string GatherFormatted()
    {
        var sb = new StringBuilder();
        sb.Append("---- Auto-collected system info ----\r\n");
        foreach (var (label, value) in GatherKvp())
        {
            sb.Append(label.PadRight(13));
            sb.Append(" : ");
            sb.Append(value);
            sb.Append("\r\n");
        }
        sb.Append("------------------------------------\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Same data as <see cref="GatherFormatted"/> but as a structured list
    /// so HTML / JSON renderers can lay it out themselves instead of having
    /// to parse the formatted block back apart. Adding a field is a one-line
    /// change here that benefits both renderers.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> GatherKvp()
    {
        return new (string, string)[]
        {
            ("Computer name", ComputerName),
            ("Username",      UserName),
            ("User domain",   UserDomain),
            ("Serial number", SerialNumber),
            ("OS",            OsDescription),
            ("Wi-Fi SSID",    WifiSsid),
            ("IPv4 address",  IPv4Address),
            ("App version",   AppVersion),
            ("Local time",    Timestamp),
        };
    }

    private static string Safe(Func<string> f)
    {
        try
        {
            string s = f();
            return string.IsNullOrWhiteSpace(s) ? "(unknown)" : s;
        }
        catch
        {
            return "(unknown)";
        }
    }

    // -------- Device serial --------

    private static string GetBiosSerial()
    {
        // Win32_BIOS.SerialNumber matches the asset tag on the chassis on
        // most Dell / Lenovo / HP corporate fleets. Some VMs return "0" or a
        // long GUID — that's fine, the helpdesk knows what to do with it.
        using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
        foreach (ManagementObject obj in searcher.Get())
        {
            string? sn = obj["SerialNumber"]?.ToString();
            if (!string.IsNullOrWhiteSpace(sn)) return sn.Trim();
        }
        return "(unknown)";
    }

    // -------- OS --------

    private static string GetOsDescription()
        => RuntimeInformation.OSDescription;

    // -------- Wi-Fi --------

    private static string GetWifiSsid()
    {
        // Shelling out to netsh is the simplest reliable way to read the
        // current SSID without managed Wi-Fi APIs (which require extra
        // packages and admin in some configs). Bounded so a hung netsh
        // never stalls the email build.
        var psi = new ProcessStartInfo
        {
            FileName               = "netsh",
            Arguments              = "wlan show interfaces",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return "(unknown)";

        string output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(2000))
        {
            try { proc.Kill(); } catch { /* ignore */ }
            return "(unknown)";
        }

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();

            // Skip "BSSID : ..." — we want the friendly SSID.
            if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase)) continue;

            if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase))
            {
                int idx = line.IndexOf(':');
                if (idx > 0 && idx < line.Length - 1)
                {
                    string ssid = line[(idx + 1)..].Trim();
                    if (!string.IsNullOrEmpty(ssid)) return ssid;
                }
            }
        }
        return "(not connected to Wi-Fi)";
    }

    // -------- Local IP --------

    private static string GetLocalIPv4()
    {
        var addrs = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    addrs.Add(ip.Address.ToString());
            }
        }

        return addrs.Count == 0 ? "(unknown)" : string.Join(", ", addrs);
    }
}
