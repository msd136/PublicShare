using System;
using Microsoft.Win32;

namespace HelpdeskHeroes;

/// <summary>
/// Reads runtime configuration from the registry. A deployment script writes
/// these values to <c>HKLM\SOFTWARE\HelpdeskHeroes</c> as part of install —
/// that keeps the SMTP2GO API key out of the EXE bytes (where any user with
/// a hex editor could pull it) and lets ops rotate the key without
/// recompiling and redeploying the app.
///
/// Threat-model note: HKLM\SOFTWARE is readable by all logged-in users by
/// default. A determined user CAN read these values via regedit. The real
/// defense lives on the SMTP2GO side: lock the API key to a single sender,
/// IP-allowlist your egress, and rate-limit the key. The registry just keeps
/// the key out of the binary.
///
/// All getters fall back gracefully — if a value is missing, the calling
/// code (EmailSender) surfaces a friendly error instead of throwing on
/// startup, so a misconfigured device still launches and the user can still
/// copy-paste the ticket from the SentPage failure view.
/// </summary>
internal static class Configuration
{
    private const string RegistryPath = @"SOFTWARE\HelpdeskHeroes";

    // Value names — keep in sync with your deployment script.
    private const string ValueApiKey       = "Smtp2GoApiKey";
    private const string ValueSenderDomain = "SenderDomain";
    private const string ValueHelpdeskTo   = "HelpdeskRecipient";

    // Fallbacks for development / unconfigured devices.
    //
    // TODO: Replace these with values appropriate for your organization, OR
    // leave them as placeholders and rely entirely on the registry-populated
    // values at runtime. The app still launches with these defaults, but
    // the email send will obviously go nowhere useful until you point them
    // at a real domain and inbox.
    private const string DefaultSenderDomain = "example.com";
    private const string DefaultHelpdeskTo   = "helpdesk@example.com";

    /// <summary>
    /// SMTP2GO API key. Returns null if not configured — caller (EmailSender)
    /// must treat null as "deployment is misconfigured" and surface that
    /// state to the user via the existing SentPage failure path.
    /// </summary>
    public static string? Smtp2GoApiKey => ReadString(ValueApiKey);

    /// <summary>
    /// Domain to append to a bare SAM account name to build the user's
    /// From address (e.g. "jsmith" + "example.com" → "jsmith@example.com").
    /// Falls back to a hardcoded default so dev builds work without a
    /// deployment run.
    /// </summary>
    public static string SenderDomain =>
        ReadString(ValueSenderDomain) ?? DefaultSenderDomain;

    /// <summary>
    /// Helpdesk recipient address. Configurable so a deployment can repoint
    /// it (e.g. to a Jira Service Desk inbound address) without a rebuild.
    /// </summary>
    public static string HelpdeskRecipient =>
        ReadString(ValueHelpdeskTo) ?? DefaultHelpdeskTo;

    private static string? ReadString(string valueName)
    {
        try
        {
            // Always read 64-bit view — most modern deployment tooling runs
            // 64-bit, and we ship as x64. Reading the default view on a
            // 32-bit-emulated process would land us in the WOW6432Node hive
            // and miss the key.
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryPath, writable: false);
            if (key == null) return null;

            var raw = key.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return raw.Trim();
        }
        catch
        {
            // Registry access can fail on locked-down endpoints in surprising
            // ways (group policy auditing, EDR shims, etc.). Treat any failure
            // as "not configured" — the email send will then go down the
            // friendly-error path.
            return null;
        }
    }
}
