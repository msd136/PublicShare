using System;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;

namespace HelpdeskHeroes;

/// <summary>
/// Resolves the signed-in user's email address for use as the From line
/// on outgoing helpdesk tickets. The strategy mirrors how an Outlook profile
/// figures out "who am I":
///
///   1. WindowsIdentity.GetCurrent().Name — for AAD/Entra-joined devices,
///      this is already in UPN form (jsmith@example.com). Fast path,
///      no AD round-trip.
///   2. UserPrincipal.Current.UserPrincipalName — for AD-joined devices on
///      a corporate network, this hits a DC and returns the canonical UPN.
///      Skipped silently if there's no DC reachable (offline laptop).
///   3. Synthesize <c>SAM@SenderDomain</c> as a last resort. This is what
///      most tenants use anyway, so it's usually right even when the lookup
///      paths above fail.
///
/// All paths are wrapped — same defensive style as SystemInfo.cs — because
/// users will run this on offline laptops, devices that just rejoined a
/// domain, devices where the local profile is brand-new, etc. Returning a
/// plausible synthesized address is always better than crashing the email
/// build.
/// </summary>
internal static class UserIdentity
{
    /// <summary>
    /// Best guess at the user's SMTP address. Always returns something
    /// non-empty — never null, never throws.
    /// </summary>
    public static string ResolveEmail()
    {
        // 1. Fast path: WindowsIdentity often already carries a UPN on
        //    Entra-joined / hybrid-joined devices. No network call.
        try
        {
            string name = WindowsIdentity.GetCurrent().Name ?? "";
            if (LooksLikeUpn(name)) return name;
        }
        catch { /* fall through */ }

        // 2. Slower path: ask AD via UserPrincipal. This will block on a
        //    DC discovery if the device is in a workgroup or has lost its
        //    trust relationship — so we gate it on the device actually
        //    being domain-joined first. The catch handles every known
        //    failure mode (InvalidCastException on Entra-only boxes, DC
        //    lookup timeouts on offline laptops, etc.).
        if (IsDomainJoined())
        {
            try
            {
                using var ctx  = new PrincipalContext(ContextType.Domain);
                using var user = UserPrincipal.Current;
                if (user != null)
                {
                    string? upn = user.UserPrincipalName;
                    if (LooksLikeUpn(upn)) return upn!;

                    // Some tenants provision EmailAddress instead of UPN.
                    string? mail = user.EmailAddress;
                    if (LooksLikeUpn(mail)) return mail!;
                }
            }
            catch { /* offline / locked down — fall through */ }
        }

        // 3. Synthesize from SAM account name + configured domain.
        return $"{StripDomainPrefix(Environment.UserName)}@{Configuration.SenderDomain}";
    }

    /// <summary>
    /// Cheap "is this box on a domain?" check that doesn't trigger DC discovery.
    /// Reads the Domain property from the local computer's WMI record. The
    /// "Workgroup" string is what WMI reports for non-domain devices.
    /// </summary>
    private static bool IsDomainJoined()
    {
        try
        {
            // Environment.UserDomainName returns the machine name on a
            // workgroup device, and the AD/Entra domain on a domain-joined
            // device. Comparing it against MachineName is a fast, free
            // proxy for "are we domain-joined".
            return !string.Equals(
                Environment.UserDomainName,
                Environment.MachineName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True if <paramref name="s"/> looks like a deliverable address. We're
    /// lenient on purpose — RFC 5322 validation is overkill here, and the
    /// synthesized fallback would catch a malformed UPN anyway.
    /// </summary>
    private static bool LooksLikeUpn(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        int at = s.IndexOf('@');
        // Reject DOMAIN\user (backslash form) and bare account names.
        if (at <= 0 || at == s.Length - 1) return false;
        // Reject DOMAIN\user@something (paranoid).
        if (s.IndexOf('\\') >= 0) return false;
        return true;
    }

    /// <summary>
    /// "DOMAIN\jsmith" → "jsmith". Environment.UserName is normally bare,
    /// but a few odd contexts (impersonation, scheduled tasks) leak the
    /// down-level form through.
    /// </summary>
    private static string StripDomainPrefix(string sam)
    {
        if (string.IsNullOrEmpty(sam)) return "user";
        int slash = sam.IndexOf('\\');
        return slash >= 0 ? sam[(slash + 1)..] : sam;
    }
}
