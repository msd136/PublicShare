using System.Data;
using System.Globalization;
using GamGui.Core;

namespace GamGui;

/// <summary>
/// A ready-made job. <see cref="How"/> tells the user which GAM command runs and what it does,
/// so they learn GAM as they go without needing to know it up front.
/// </summary>
public sealed record QuickTask(
    string Category,
    string Title,
    string What,
    string How,
    Field[] Fields,
    Func<Dictionary<string, string>, List<GamCommand>> Build,
    bool Changes = false,
    Func<DataTable, Dictionary<string, string>, DataTable>? Transform = null);

public static class QuickTasks
{
    public const string Reports = "Reports";
    public const string Accounts = "Accounts and security";
    public const string Mail = "Gmail and Drive";
    public const string Groups = "Groups";
    public const string Devices = "Devices";
    public const string Classroom = "Classroom";

    public static readonly string[] Categories = { Reports, Accounts, Mail, Groups, Devices, Classroom };

    private static Field User(string label = "User's email address") =>
        new("user", label, Required: true);

    private static List<GamCommand> One(params string[] args) => new() { new GamCommand(args) };

    public static readonly QuickTask[] All =
    {
        // ---- Reports (read only) ----
        new(Reports, "All users",
            "Lists every account with its name, org unit, status and last sign-in.",
            "Runs \"gam print users fields primaryemail,name,suspended,orgunitpath,lastlogintime\". GAM asks Google's Directory for every user and returns one row each.",
            Array.Empty<Field>(),
            _ => One("print", "users", "fields", "primaryemail,name,suspended,orgunitpath,isadmin,lastlogintime")),

        new(Reports, "Suspended users",
            "Lists accounts that are currently suspended, so you can review or clean them up.",
            "Runs \"gam print users issuspended true\", which asks Google for suspended accounts only.",
            Array.Empty<Field>(),
            _ => One("print", "users", "issuspended", "true", "fields", "primaryemail,name,orgunitpath,lastlogintime")),

        new(Reports, "Users who haven't signed in recently",
            "Finds accounts with no sign-in for a number of days, including accounts that have never signed in. Useful for licence clean-up and security reviews.",
            "Runs \"gam print users fields …,lastlogintime\" for everyone, then this app keeps only the rows older than your cut-off. Google's last sign-in time can lag by up to a day.",
            new[]
            {
                new Field("days", "Not signed in for at least this many days", "90", Required: true),
                new Field("active", "Leave out accounts that are already suspended", "true", FieldKind.Check),
            },
            v =>
            {
                var args = new List<string> { "print", "users" };
                if (v["active"] == "true") args.AddRange(new[] { "issuspended", "false" });
                args.AddRange(new[] { "fields", "primaryemail,name,orgunitpath,suspended,lastlogintime,creationtime" });
                return new List<GamCommand> { new(args) };
            },
            Transform: FilterInactive),

        new(Reports, "2-Step Verification status",
            "Shows who has enrolled in 2-Step Verification and whether it's enforced for them.",
            "Runs \"gam print users fields primaryemail,name,isenrolledin2sv,isenforcedin2sv,orgunitpath\".",
            Array.Empty<Field>(),
            _ => One("print", "users", "fields", "primaryemail,name,isenrolledin2sv,isenforcedin2sv,orgunitpath")),

        new(Reports, "Administrators and their roles",
            "Lists every admin role assignment: who is an admin, which role they hold and where it applies.",
            "Runs \"gam print admins\", which reads the role assignments from Google's Directory.",
            Array.Empty<Field>(),
            _ => One("print", "admins")),

        new(Reports, "Licences assigned",
            "Lists which Google Workspace licences are assigned to which users. Large domains can take a while.",
            "Runs \"gam print licenses\", which checks each of your organization's licence products.",
            Array.Empty<Field>(),
            _ => One("print", "licenses")),

        new(Reports, "Domains",
            "Lists the domains on your account and whether each is verified.",
            "Runs \"gam print domains\".",
            Array.Empty<Field>(),
            _ => One("print", "domains")),

        new(Reports, "Shared drives",
            "Lists every shared drive in your organization.",
            "Runs \"gam print shareddrives\".",
            Array.Empty<Field>(),
            _ => One("print", "shareddrives")),

        // ---- Accounts and security ----
        new(Accounts, "Offboard a departing user",
            "Locks a leaver out and hands over their work in one go. Choose which steps to include; each is explained in the form.",
            "Builds a list of GAM commands from your answers and runs them in order: \"update user … password random\", \"user … deprovision signout turnoff2sv\", \"create datatransfer … drive …\", \"user … vacation on …\", \"update user … org …\" and \"update user … suspended on\". You'll see the full list before anything runs.",
            new[]
            {
                User("Departing user's email address"),
                new Field("password", "Change their password to a random one", "true", FieldKind.Check),
                new Field("deprovision", "Sign them out everywhere and remove app passwords, backup codes and app access", "true", FieldKind.Check),
                new Field("newowner", "Transfer their Drive files to", Hint: "Usually their manager. Leave empty to skip."),
                new Field("reply", "Auto-reply message", Kind: FieldKind.MultiLine,
                    Hint: "For example: I've left the company. Please contact … Leave empty to skip."),
                new Field("ou", "Move them to org unit", Hint: "For example /Former staff. Leave empty to skip."),
                new Field("suspend", "Suspend the account (note: suspended accounts don't send auto-replies)", "false", FieldKind.Check),
            },
            v =>
            {
                var u = v["user"];
                var list = new List<GamCommand>();
                if (v["password"] == "true") list.Add(GamCommand.Of("update", "user", u, "password", "random"));
                if (v["deprovision"] == "true") list.Add(GamCommand.Of("user", u, "deprovision", "signout", "turnoff2sv"));
                if (v["newowner"].Length > 0) list.Add(GamCommand.Of("create", "datatransfer", u, "drive", v["newowner"], "all"));
                if (v["reply"].Length > 0) list.Add(GamCommand.Of("user", u, "vacation", "on", "subject", "Out of office", "message", v["reply"]));
                if (v["ou"].Length > 0) list.Add(GamCommand.Of("update", "user", u, "org", v["ou"]));
                if (v["suspend"] == "true") list.Add(GamCommand.Of("update", "user", u, "suspended", "on"));
                return list;
            },
            Changes: true),

        new(Accounts, "Sign a user out everywhere",
            "Ends the user's sessions on every browser and device. Use it after a password reset or if an account may be compromised.",
            "Runs \"gam user <email> signout\". The user has to sign in again everywhere.",
            new[] { User() },
            v => One("user", v["user"], "signout"),
            Changes: true),

        new(Accounts, "Turn off 2-Step Verification for a user",
            "Lets a user who lost their phone sign in with just their password so they can set 2-Step Verification up again.",
            "Runs \"gam user <email> turnoff2sv\". If 2-Step Verification is enforced for them, they'll be asked to enroll again at sign-in.",
            new[] { User() },
            v => One("user", v["user"], "turnoff2sv"),
            Changes: true),

        new(Accounts, "Show a user's backup codes",
            "Shows the user's 2-Step Verification backup codes so you can read one to them over the phone.",
            "Runs \"gam user <email> print backupcodes\". If the user has none, the list is empty.",
            new[] { User() },
            v => One("user", v["user"], "print", "backupcodes")),

        new(Accounts, "Apps a user has given access to",
            "Lists the third-party apps the user has signed in to with their Google account, and what each can access.",
            "Runs \"gam user <email> print tokens\".",
            new[] { User() },
            v => One("user", v["user"], "print", "tokens")),

        // ---- Gmail and Drive ----
        new(Mail, "Set an out-of-office reply",
            "Turns on the vacation responder for a user, for example while they're away or after they leave.",
            "Runs \"gam user <email> vacation on subject … message …\" using GAM's service account.",
            new[]
            {
                User(),
                new Field("subject", "Subject", "Out of office", Required: true),
                new Field("message", "Message", Kind: FieldKind.MultiLine, Required: true),
            },
            v => One("user", v["user"], "vacation", "on", "subject", v["subject"], "message", v["message"]),
            Changes: true),

        new(Mail, "Turn off an out-of-office reply",
            "Turns the vacation responder off.",
            "Runs \"gam user <email> vacation off\".",
            new[] { User() },
            v => One("user", v["user"], "vacation", "off"),
            Changes: true),

        new(Mail, "Give someone access to a mailbox",
            "Lets one person read, send and delete mail in another person's mailbox (Gmail delegation).",
            "Runs \"gam user <mailbox> add delegate <person>\". The delegate sees the mailbox in their account menu within about an hour.",
            new[]
            {
                User("Mailbox (whose mail)"),
                new Field("delegate", "Person who gets access", Required: true),
            },
            v => One("user", v["user"], "add", "delegate", v["delegate"]),
            Changes: true),

        new(Mail, "Transfer Drive files to another user",
            "Moves ownership of all of one user's Drive files to another user.",
            "Runs \"gam create datatransfer <from> drive <to> all\". Google does the transfer in the background and emails both people when it's done.",
            new[]
            {
                User("Current owner"),
                new Field("to", "New owner", Required: true),
            },
            v => One("create", "datatransfer", v["user"], "drive", v["to"], "all"),
            Changes: true),

        // ---- Groups ----
        new(Groups, "Groups a user belongs to",
            "Lists every group that has the user as a member.",
            "Runs \"gam print groups member <email> fields email,name\".",
            new[] { User() },
            v => One("print", "groups", "member", v["user"], "fields", "email,name,description")),

        new(Groups, "Add one person to several groups",
            "Adds a user to a list of groups in one go, for example for a new starter.",
            "Runs \"gam update group <group> add <role> <email>\" once for each group.",
            new[]
            {
                User(),
                new Field("groups", "Group email addresses", Kind: FieldKind.MultiLine, Required: true,
                    Hint: "Separate with commas, spaces or new lines."),
                new Field("role", "Role", "member", FieldKind.Choice, new[] { "member", "manager", "owner" }),
            },
            v => FormDialog.SplitList(v["groups"])
                .Select(g => GamCommand.Of("update", "group", g, "add", v["role"], v["user"])).ToList(),
            Changes: true),

        // ---- Devices ----
        new(Devices, "Find a Chromebook by serial number",
            "Looks up a ChromeOS device from the serial number on its label.",
            "Runs \"gam print cros query id:<serial>\", which searches Google's device list by serial number.",
            new[] { new Field("serial", "Serial number", Required: true) },
            v => One("print", "cros", "query", "id:" + v["serial"], "fields",
                "deviceid,serialnumber,model,status,orgunitpath,annotateduser,lastsync")),

        new(Devices, "A user's mobile devices",
            "Lists the phones and tablets a user has signed in to with their work account.",
            "Runs \"gam print mobile query email:<email>\".",
            new[] { User() },
            v => One("print", "mobile", "query", "email:" + v["user"], "fields",
                "resourceid,model,os,type,status,lastsync")),

        // ---- Classroom ----
        new(Classroom, "Classroom courses",
            "Lists courses with their owner. Optionally only one teacher's courses, or only active ones.",
            "Runs \"gam print courses owneremail\" with \"teacher <email>\" and \"states active\" if you choose them. Classroom data access must be on for GAM's admin account.",
            new[]
            {
                new Field("teacher", "Only courses taught by", Hint: "A teacher's email address. Leave empty for all courses."),
                new Field("active", "Active courses only", "true", FieldKind.Check),
            },
            v =>
            {
                var args = new List<string> { "print", "courses" };
                if (v["teacher"].Length > 0) args.AddRange(new[] { "teacher", v["teacher"] });
                if (v["active"] == "true") args.AddRange(new[] { "states", "active" });
                args.Add("owneremail");
                return new List<GamCommand> { new(args) };
            }),
    };

    /// <summary>Keeps users whose last sign-in is older than the cut-off, or who never signed in.</summary>
    private static DataTable FilterInactive(DataTable table, Dictionary<string, string> v)
    {
        if (!int.TryParse(v["days"], out var days) || days < 0) days = 90;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var col = CsvTable.FindColumn(table, "lastLoginTime");
        if (col == null) return table;
        var result = table.Clone();
        foreach (DataRow row in table.Rows)
        {
            var text = row[col.ColumnName]?.ToString() ?? "";
            bool never = text.Length == 0 || text.Equals("Never", StringComparison.OrdinalIgnoreCase) || text.StartsWith("1970");
            bool old = DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) && when < cutoff;
            if (never || old) result.ImportRow(row);
        }
        return result;
    }
}
