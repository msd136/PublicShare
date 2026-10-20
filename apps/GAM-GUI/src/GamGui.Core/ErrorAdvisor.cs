namespace GamGui.Core;

/// <summary>A suggested fix the app can run for the user.</summary>
/// <param name="Label">Button text, e.g. "Re-authorize GAM".</param>
/// <param name="Explanation">What the button does, in plain words.</param>
/// <param name="Args">GAM arguments. "{user}" is replaced with an email the user enters.</param>
/// <param name="InTerminal">True when GAM will ask questions or open a browser.</param>
public sealed record FixAction(string Label, string Explanation, string[] Args, bool InTerminal)
{
    public bool NeedsUser => Args.Contains("{user}");
}

/// <summary>A recognized problem, explained for an administrator rather than a developer.</summary>
public sealed record ErrorAdvice(
    string Id,
    string Title,
    string Explanation,
    IReadOnlyList<string> Steps,
    string? HelpUrl = null,
    string? HelpLabel = null,
    FixAction? Fix = null)
{
    /// <summary>The line of GAM output that matched, shown so the user can see why.</summary>
    public string MatchedLine { get; init; } = "";
}

/// <summary>
/// Matches GAM's output against known Google Workspace and GAM errors. The patterns come
/// from GAM's own messages (gamlib/glmsgs.py and glapi.py) and Google's API error texts.
/// Rules are checked in order, so the more specific ones come first.
/// </summary>
public static class ErrorAdvisor
{
    private const string Wiki = "https://github.com/GAM-team/GAM/wiki/";

    private sealed record Rule(string[] Patterns, ErrorAdvice Advice);

    private static readonly FixAction Reauthorize = new(
        "Re-authorize GAM",
        "Runs \"gam oauth create\" in a console window. GAM lists its scopes, you pick the ones you need, and your browser opens so you can sign in as an administrator and approve them.",
        new[] { "oauth", "create" }, InTerminal: true);

    private static readonly FixAction CheckServiceAccount = new(
        "Check the service account",
        "Runs \"gam user <email> check serviceaccount\" in a console window. GAM tests every scope and, for any that fail, prints the exact Admin console link and the scopes to paste in.",
        new[] { "user", "{user}", "check", "serviceaccount" }, InTerminal: true);

    private static readonly List<Rule> Rules = new()
    {
        new(new[] { "@ClassroomApiDisabled" }, new ErrorAdvice(
            "classroom-api-disabled",
            "Classroom data access is turned off for this account",
            "Google blocked the request because the account GAM signs in as isn't allowed to use the Classroom API. This is an Admin console setting, separate from the scopes you chose when you authorized GAM.",
            new[]
            {
                "In the Google Admin console, open Apps > Google Workspace > Classroom > Data access.",
                "Allow users to authorize apps to access their Google Classroom data (the Classroom API setting).",
                "Make sure the setting applies to the org unit that contains the admin account GAM uses. Settings > Admin authorization shows which account that is.",
                "Wait for the change to take effect (usually minutes, occasionally up to a day), then try again.",
            },
            "https://support.google.com/edu/classroom/answer/6250906", "Google: enable Classroom data access")),

        new(new[] { "@ClassroomDisabled" }, new ErrorAdvice(
            "classroom-disabled",
            "Classroom is turned off for this account",
            "The account making the request doesn't have the Classroom service. It may be in an org unit where Classroom is off, or GAM may be signed in with a different account than you expect.",
            new[]
            {
                "In the Admin console, open Apps > Google Workspace > Classroom > Service status.",
                "Turn Classroom on for the org unit of the account GAM uses.",
                "Check Settings > Admin authorization to confirm which account GAM is using.",
            },
            "https://support.google.com/edu/classroom/answer/6023715", "Google: turn Classroom on or off")),

        new(new[] { "your system time differs" }, new ErrorAdvice(
            "clock",
            "This computer's clock is wrong",
            "Google's sign-in tokens are time-stamped, so a clock that's off by more than a few minutes makes every request fail.",
            new[]
            {
                "Open Windows Settings > Time & language > Date & time.",
                "Turn on \"Set time automatically\" and choose \"Sync now\".",
                "Try the command again.",
            })),

        new(new[] { "unauthorized_client", "client is unauthorized to retrieve access tokens", "not authorized for any of the scopes requested" },
            new ErrorAdvice(
            "dwd-scopes",
            "The service account isn't authorized for this",
            "Commands that act inside a user's Drive, Gmail or Calendar use GAM's service account with domain-wide delegation. Google says the service account hasn't been granted the scope this command needs.",
            new[]
            {
                "Choose \"Check the service account\" below and enter any user in your domain.",
                "GAM shows which scopes pass and which fail, with a link to the Admin console's domain-wide delegation page.",
                "Follow that link, paste the scopes GAM lists into the service account's entry, and save.",
                "Run the check again until every scope passes.",
            },
            Wiki + "Authorization", "GAM wiki: Authorization", CheckServiceAccount)),

        new(new[] { "please run \"gam update project\"", "has not been used in project", "accessnotconfigured", "service_disabled", "it is disabled. enable it by visiting" },
            new ErrorAdvice(
            "api-not-enabled",
            "An API is turned off in GAM's Google Cloud project",
            "Each Google service GAM uses must be enabled in the Cloud project created during setup. This one isn't, usually because GAM added support for a new API after your project was created.",
            new[]
            {
                "Choose \"Enable GAM's APIs\" below. GAM turns on every API it uses in your project.",
                "If GAM also mentions the service account, run Settings > Check the service account afterwards.",
                "Newly enabled APIs can take a few minutes to start working.",
            },
            Wiki + "Authorization", "GAM wiki: Authorization",
            new FixAction("Enable GAM's APIs",
                "Runs \"gam update project\" in a console window. GAM enables every API it needs in your Cloud project; you may be asked to sign in.",
                new[] { "update", "project" }, InTerminal: true))),

        new(new[] { "is expired or has been revoked", "token has been expired or revoked", "rapt_required", "invalid_rapt" },
            new ErrorAdvice(
            "token-expired",
            "GAM's admin authorization has expired",
            "The saved sign-in (oauth2.txt) is no longer valid. This happens when the admin's password changes, the authorization is revoked in the Admin console, or a session-length policy requires signing in again.",
            new[]
            {
                "Choose \"Re-authorize GAM\" below and sign in as the same administrator.",
                "Select the scopes you use; GAM remembers them as the defaults.",
                "If your organization limits session length for admins, you'll need to do this periodically.",
            },
            Wiki + "Authorization", "GAM wiki: Authorization", Reauthorize)),

        new(new[] { "oauth2service.json", "gam user <user> update serviceaccount" }, new ErrorAdvice(
            "no-service-account",
            "GAM's service account isn't set up",
            "This command acts on a user's own data (Drive, Gmail, Calendar…) and needs GAM's service account file, oauth2service.json, which wasn't found.",
            new[]
            {
                "If you haven't finished GAM's setup, open Settings and run the three first-time setup steps.",
                "If GAM was set up elsewhere, copy oauth2service.json into GAM's config folder, or point Settings > Config folder at the folder that has it.",
            },
            Wiki + "How-to-Install-GAM7", "GAM wiki: install and set up GAM7")),

        new(new[] { "client_secrets.json", "gam create|use project" }, new ErrorAdvice(
            "no-project",
            "GAM hasn't been connected to Google yet",
            "GAM needs a Google Cloud project and an admin authorization before it can do anything. The files that hold them (client_secrets.json and oauth2.txt) weren't found.",
            new[]
            {
                "Open Settings > First-time setup and run step 1 (create the Cloud project), then step 2 (authorize admin access).",
                "If GAM was set up under a different folder, set Settings > Config folder to that folder instead.",
            },
            Wiki + "How-to-Install-GAM7", "GAM wiki: install and set up GAM7")),

        new(new[] { "request had insufficient authentication scopes", "insufficientpermissions", "insufficient permission", "access_token_scope_insufficient" },
            new ErrorAdvice(
            "missing-scope",
            "GAM wasn't given permission for this area",
            "When GAM was authorized, the scope this command needs wasn't selected. Re-authorizing lets you add it without losing the others.",
            new[]
            {
                "Choose \"Add scopes\" below. A console window shows GAM's scope list with your current choices marked.",
                "Select the scope for this area (for example Classroom, Vault or Chrome), then continue and approve in the browser.",
                "Run the command again.",
            },
            Wiki + "Authorization", "GAM wiki: Authorization",
            new FixAction("Add scopes",
                "Runs \"gam oauth update\" in a console window, which reopens the scope list so you can add what's missing and approve it in your browser.",
                new[] { "oauth", "update" }, InTerminal: true))),

        new(new[] { "not authorized to access this resource/api", "insufficient permissions to perform this task" },
            new ErrorAdvice(
            "admin-privileges",
            "Your admin account doesn't have the privilege for this",
            "Google accepted GAM's sign-in but the administrator it signs in as isn't allowed to do this. Delegated admins only get the privileges in their role.",
            new[]
            {
                "In the Admin console, open Account > Admin roles and check the role assigned to GAM's admin account.",
                "Add the privilege for this area, or authorize GAM with a super administrator.",
                "Settings > Admin authorization shows which account GAM uses.",
            },
            "https://support.google.com/a/answer/2405986", "Google: pre-built administrator roles")),

        new(new[] { "api access denied", "access_denied" }, new ErrorAdvice(
            "api-access-denied",
            "Google refused GAM's access",
            "The admin authorization exists but Google denied it. Common causes are the GAM project being blocked under API controls, the admin account being restricted, or an authorization that was revoked.",
            new[]
            {
                "In the Admin console, open Security > Access and data control > API controls and make sure GAM's project isn't blocked.",
                "Choose \"Re-authorize GAM\" below to sign in again.",
                "If the message mentions the service account, run Settings > Check the service account as well.",
            },
            Wiki + "Authorization", "GAM wiki: Authorization", Reauthorize)),

        new(new[] { "service/app not enabled", "mail service not enabled", "service not applicable", "domain cannot use apis", "domaincannotuseapis" },
            new ErrorAdvice(
            "service-off-for-user",
            "That service isn't available for this user",
            "The user doesn't have the Google service this command uses. Usually the service is turned off for their org unit, they don't have a license that includes it, or the account is suspended.",
            new[]
            {
                "In the Admin console, check Apps > Google Workspace > (the service) > Service status for the user's org unit.",
                "Check the user's licenses under Users > (the user) > Licenses.",
                "Make sure the user isn't suspended.",
            })),

        new(new[] { "quota exceeded", "ratelimitexceeded", "rate limit exceeded", "userratelimitexceeded", "resource_exhausted", "too many requests" },
            new ErrorAdvice(
            "quota",
            "Google is limiting how fast requests can be made",
            "Google Workspace APIs have per-minute and per-day limits. GAM retries automatically, but very large jobs can still hit them.",
            new[]
            {
                "Wait a few minutes and try again.",
                "For large jobs, work in smaller batches (for example one org unit at a time).",
                "Daily limits reset at midnight Pacific time.",
            })),

        new(new[] { "failed to establish a new connection", "nameresolutionerror", "getaddrinfo failed", "connection refused", "certificate_verify_failed", "max retries exceeded", "unable to find the server", "timed out" },
            new ErrorAdvice(
            "network",
            "GAM couldn't reach Google",
            "The request didn't get to Google's servers. The internet connection may be down, or a proxy or firewall may be blocking or inspecting the traffic.",
            new[]
            {
                "Choose \"Test the connection\" below. GAM checks each Google address it needs and reports which ones fail.",
                "If you're behind a proxy, set it in gam.cfg or in the HTTPS_PROXY environment variable.",
                "Firewalls that inspect HTTPS traffic can cause certificate errors; ask your network team to allow Google APIs.",
            },
            Fix: new FixAction("Test the connection",
                "Runs \"gam checkconnection\", which tries every Google host GAM uses and shows the result for each.",
                new[] { "checkconnection" }, InTerminal: false))),

        new(new[] { "already exists", "entity already exists", "duplicate", "member already exists" },
            new ErrorAdvice(
            "already-exists",
            "That already exists",
            "Google already has something with that name or address, so nothing was created.",
            new[]
            {
                "Check the spelling, or search for the existing item on the relevant screen.",
                "Email addresses must be unique across users, groups and aliases.",
            })),

        new(new[] { "does not exist", "resource not found", "notfound", "entity not found", "invalid input: email" },
            new ErrorAdvice(
            "not-found",
            "Google couldn't find that",
            "The user, group, org unit, device or other item named in the command doesn't exist, or isn't visible to GAM's admin account.",
            new[]
            {
                "Check the spelling. Email addresses must include the domain, and org unit paths start with / (for example /Staff/Teachers).",
                "Load the list on the matching screen and pick the item from there instead of typing it.",
            })),

        new(new[] { "invalid password", "password is too weak", "password must be" },
            new ErrorAdvice(
            "password-policy",
            "The password doesn't meet your domain's rules",
            "Google rejected the password because of your organization's password policy.",
            new[]
            {
                "Use a longer password, or keep the random one the app suggests.",
                "Your rules are in the Admin console under Security > Authentication > Password management.",
            })),

        new(new[] { ">>>", "<<<" }, new ErrorAdvice(
            "bad-argument",
            "GAM didn't understand part of the command",
            "GAM marks the part it couldn't read with >>> and <<<. It's usually a misspelled option, a value in the wrong place, or something the installed version of GAM doesn't support.",
            new[]
            {
                "Look at the line that starts with \"Command:\" below to see which word GAM stopped at.",
                "Open the command in All commands to fill it in with a form instead of typing it.",
                "If the option is new, update GAM; the forms follow the GamCommands.txt installed with it.",
            })),
    };

    /// <summary>Returns advice for the first known problem in GAM's output, or null.</summary>
    public static ErrorAdvice? Diagnose(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = output.Replace("\r", "").Split('\n');
        foreach (var rule in Rules)
        {
            // The argument-error rule needs both markers on the same line.
            if (rule.Advice.Id == "bad-argument")
            {
                var hit = lines.FirstOrDefault(l => l.Contains(">>>") && l.Contains("<<<"));
                if (hit != null) return rule.Advice with { MatchedLine = hit.Trim() };
                continue;
            }
            foreach (var pattern in rule.Patterns)
            {
                var hit = lines.FirstOrDefault(l => l.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return rule.Advice with { MatchedLine = hit.Trim() };
            }
        }
        return null;
    }

    /// <summary>Convenience overload for a finished run; only failed runs or runs with errors are checked.</summary>
    public static ErrorAdvice? Diagnose(GamResult result)
    {
        if (result.Cancelled) return null;
        var text = result.StdErr + "\n" + (result.ExitCode != 0 ? result.StdOut : "");
        bool looksFailed = result.ExitCode != 0 || result.StdErr.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                           || result.StdErr.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                           || result.StdErr.Contains("Failed", StringComparison.OrdinalIgnoreCase);
        return looksFailed ? Diagnose(text) : null;
    }

    /// <summary>Number of known problems; shown in the About text.</summary>
    public static int RuleCount => Rules.Count;
}
