using System.Windows;
using System.Windows.Controls;
using GamGui.Core;

namespace GamGui.Views;

public sealed class UsersView : ViewBase
{
    private readonly HintBox _query = new("Search, e.g. name:Jane or orgUnitPath=/Sales", 320);
    private readonly HintBox _ou = new("Limit to org unit, e.g. /Sales", 200);
    private readonly ComboBox _status = Ui.Combo(new[] { "All users", "Active only", "Suspended only" }, 0, 140);
    private readonly ResultView _results = new("Choose Load users to list accounts from your domain.");
    private readonly TextBlock _selection = Ui.Text("No users selected", "Muted");
    private readonly Button _load;

    private static readonly string[] EmailCols = { "primaryEmail", "email" };

    public UsersView()
    {
        _load = Ui.Button("Load users", async (_, _) => await LoadAsync(), "PrimaryButton", tip: "Lists accounts matching your search. Runs: gam print users [query …] [limittoou …] fields …");
        _query.OnEnter(async () => await LoadAsync());
        _ou.OnEnter(async () => await LoadAsync());

        var toolbar = Ui.Wrap(
            Ui.Margin(_query, 0, 0, 6, 6), Ui.Margin(_ou, 0, 0, 6, 6), Ui.Margin(_status, 0, 0, 6, 6),
            _load,
            Ui.Button("New user…", async (_, _) => await CreateAsync(), tip: "Creates an account. Runs: gam create user <email> firstname … lastname … password … org …"));

        var actions = Ui.Stack(
            Ui.Text("Selected users", "H2"),
            Ui.Margin(_selection, 0, 2, 0, 12),
            Wide(Ui.Button("Details", async (_, _) => await DetailsAsync(), tip: "Shows everything about the selected user. Runs: gam info user <email>")),
            Wide(Ui.Button("Edit name…", async (_, _) => await EditNameAsync(), tip: "Changes first and last name. Runs: gam update user <email> firstname … lastname …")),
            Wide(Ui.Button("Move to org unit…", async (_, _) => await MoveAsync(), tip: "Moves the selected users. Runs: gam update user <email> org <path>")),
            Wide(Ui.Button("Reset password…", async (_, _) => await ResetPasswordAsync(), tip: "Sets a new password. Runs: gam update user <email> password … changepasswordatnextlogin on")),
            Wide(Ui.Button("Add to group…", async (_, _) => await AddToGroupAsync(), tip: "Adds the selected users to a group. Runs: gam update group <group> add <role> users <emails>")),
            Wide(Ui.Button("Suspend", async (_, _) => await SetSuspendedAsync(true), tip: "Blocks sign-in but keeps data. Runs: gam update user <email> suspended on")),
            Wide(Ui.Button("Restore", async (_, _) => await SetSuspendedAsync(false), tip: "Unsuspend the selected users")),
            Ui.Margin(Wide(Ui.Button("Delete…", async (_, _) => await DeleteAsync(), "DangerButton", tip: "Deletes the selected accounts. Runs: gam delete user <email>")), 0, 12, 0, 0));

        _results.SelectionChanged += (_, _) =>
        {
            var n = _results.SelectedRows.Count;
            _selection.Text = n == 0 ? "No users selected" : n == 1 ? _results.FirstSelected(EmailCols) ?? "1 user" : $"{n} users";
        };
        _results.RowDoubleClick += async (_, _) => await DetailsAsync();

        var body = Ui.Grid("*", "*,220");
        body.Add(_results.At(0, 0), Ui.Margin(Ui.Panel(actions), 14, 0, 0, 0).At(0, 1));

        Content = Ui.Grid("auto,auto,*").Add(
            Header("Users", "List, create and manage user accounts. Every action shows the GAM command before it runs.").At(0),
            toolbar.At(1),
            Ui.Margin(body, 0, 6, 0, 0).At(2));
    }

    private static Button Wide(Button b)
    {
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Left;
        b.Margin = new Thickness(0, 0, 0, 6);
        return b;
    }

    protected override void OnBusyChanged(bool busy) => _load.IsEnabled = !busy;

    private async Task LoadAsync()
    {
        var args = new List<string> { "print", "users" };
        if (_query.Value.Length > 0) { args.Add("query"); args.Add(_query.Value); }
        if (_ou.Value.Length > 0) { args.Add("limittoou"); args.Add(_ou.Value); }
        if (_status.SelectedIndex == 1) { args.Add("issuspended"); args.Add("false"); }
        if (_status.SelectedIndex == 2) { args.Add("issuspended"); args.Add("true"); }
        args.Add("fields");
        args.Add("primaryemail,name,suspended,orgunitpath,isadmin,lastlogintime,creationtime");
        await RunAsync(new GamCommand(args), _results);
    }

    private List<string> Selected() => RequireSelection(_results, "users", EmailCols);

    private async Task DetailsAsync()
    {
        var email = Selected().FirstOrDefault();
        if (email == null) return;
        await ShowTextOf(GamCommand.Of("info", "user", email), "User: " + email);
    }

    private async Task CreateAsync()
    {
        var v = FormDialog.Ask(OwnerWindow, "New user", "Creates the account with GAM's create user command.", "Create user",
            new Field("email", "Email address", Required: true),
            new Field("first", "First name", Required: true),
            new Field("last", "Last name", Required: true),
            new Field("password", "Password", Passwords.Generate(), FieldKind.Password, Required: true,
                Hint: "A random password is filled in. You'll see it again after the account is created."),
            new Field("ou", "Org unit", "/", Required: true),
            new Field("change", "Require a password change at next sign-in", "true", FieldKind.Check));
        if (v == null) return;

        var args = new List<string>
        {
            "create", "user", v["email"], "firstname", v["first"], "lastname", v["last"],
            "password", v["password"], "org", v["ou"],
            "changepasswordatnextlogin", v["change"] == "true" ? "on" : "off",
        };
        var cmd = new GamCommand(args, new[] { 8 });
        var r = await RunAsync(cmd, confirm: $"Create {v["email"]}?");
        if (r == null) return;
        if (r.Success)
        {
            Dialogs.ShowText(OwnerWindow, "User created",
                $"{v["email"]} was created.\n\nPassword: {v["password"]}\n\n{r.StdOut}{r.StdErr}".TrimEnd());
            await LoadAsync();
        }
        else Dialogs.ShowResult(OwnerWindow, "Create user failed", r);
    }

    private async Task EditNameAsync()
    {
        var emails = Selected();
        if (emails.Count == 0) return;
        if (emails.Count > 1) { Dialogs.Info(OwnerWindow, "Select a single user to edit their name."); return; }
        var row = _results.SelectedRows[0];
        string Cell(string caption)
        {
            var col = _results.Table == null ? null : CsvTable.FindColumn(_results.Table, caption);
            return col == null ? "" : row[col.ColumnName]?.ToString() ?? "";
        }
        var v = FormDialog.Ask(OwnerWindow, "Edit name", emails[0], "Save",
            new Field("first", "First name", Cell("name.givenName"), Required: true),
            new Field("last", "Last name", Cell("name.familyName"), Required: true));
        if (v == null) return;
        var r = await RunAsync(GamCommand.Of("update", "user", emails[0], "firstname", v["first"], "lastname", v["last"]),
            confirm: "Update this user's name?");
        await AfterChange(r);
    }

    private async Task MoveAsync()
    {
        var emails = Selected();
        if (emails.Count == 0) return;
        var v = FormDialog.Ask(OwnerWindow, "Move to org unit", $"{emails.Count} user(s) selected.", "Move",
            new Field("ou", "Org unit path", "/", Required: true, Hint: "For example /Staff/Teachers"));
        if (v == null) return;
        var cmds = emails.Select(e => GamCommand.Of("update", "user", e, "org", v["ou"])).ToList();
        if (await RunManyAsync(cmds, $"Move {emails.Count} user(s) to {v["ou"]}?", showReport: false)) await LoadAsync();
    }

    private async Task ResetPasswordAsync()
    {
        var emails = Selected();
        if (emails.Count == 0) return;
        if (emails.Count > 1) { Dialogs.Info(OwnerWindow, "Select a single user to reset their password."); return; }
        var v = FormDialog.Ask(OwnerWindow, "Reset password", emails[0], "Reset password",
            new Field("password", "New password", Passwords.Generate(), FieldKind.Password, Required: true),
            new Field("change", "Require a password change at next sign-in", "true", FieldKind.Check));
        if (v == null) return;
        var cmd = new GamCommand(new[]
        {
            "update", "user", emails[0], "password", v["password"],
            "changepasswordatnextlogin", v["change"] == "true" ? "on" : "off",
        }, new[] { 4 });
        var r = await RunAsync(cmd, confirm: $"Reset the password for {emails[0]}?");
        if (r == null) return;
        if (r.Success) Dialogs.ShowText(OwnerWindow, "Password reset", $"{emails[0]}\n\nNew password: {v["password"]}");
        else Dialogs.ShowResult(OwnerWindow, "Password reset failed", r);
    }

    private async Task AddToGroupAsync()
    {
        var emails = Selected();
        if (emails.Count == 0) return;
        var v = FormDialog.Ask(OwnerWindow, "Add to group", $"{emails.Count} user(s) selected.", "Add",
            new Field("group", "Group email", Required: true),
            new Field("role", "Role", "member", FieldKind.Choice, new[] { "member", "manager", "owner" }));
        if (v == null) return;
        var cmd = GamCommand.Of("update", "group", v["group"], "add", v["role"], "users", string.Join(",", emails));
        var r = await RunAsync(cmd, confirm: $"Add {emails.Count} user(s) to {v["group"]} as {v["role"]}?");
        if (r != null) Dialogs.ShowResult(OwnerWindow, r.Success ? "Added to group" : "Add to group failed", r);
    }

    private async Task SetSuspendedAsync(bool suspend)
    {
        var emails = Selected();
        if (emails.Count == 0) return;
        var cmds = emails.Select(e => GamCommand.Of("update", "user", e, "suspended", suspend ? "on" : "off")).ToList();
        var verb = suspend ? "Suspend" : "Restore";
        if (await RunManyAsync(cmds, $"{verb} {emails.Count} user(s)?", showReport: false)) await LoadAsync();
    }

    private async Task DeleteAsync()
    {
        var emails = Selected();
        if (emails.Count == 0) return;
        var cmds = emails.Select(e => GamCommand.Of("delete", "user", e)).ToList();
        if (await RunManyAsync(cmds,
                $"Delete {emails.Count} user(s)? Deleted users can be restored for a limited time in the Admin console.",
                showReport: false))
            await LoadAsync();
    }

    private async Task AfterChange(GamResult? r)
    {
        if (r == null) return;
        if (r.Success) await LoadAsync();
        else Dialogs.ShowResult(OwnerWindow, "GAM reported a problem", r);
    }
}
