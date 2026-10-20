using System.Windows;
using System.Windows.Controls;
using GamGui.Core;

namespace GamGui.Views;

public sealed class GroupsView : ViewBase
{
    private readonly HintBox _query = new("Search, e.g. email:sales* or name:'Field team'", 320);
    private readonly ResultView _groups = new("Choose Load groups to list your domain's groups.");
    private readonly ResultView _members = new("Select a group to see its members.");
    private readonly TextBlock _groupTitle = Ui.Text("Members", "H2");
    private readonly Button _load;
    private string? _currentGroup;
    private int _memberLoadVersion;

    private static readonly string[] GroupEmailCols = { "email" };
    private static readonly string[] MemberEmailCols = { "email", "id" };

    public GroupsView()
    {
        _load = Ui.Button("Load groups", async (_, _) => await LoadGroupsAsync(), "PrimaryButton", tip: "Lists groups matching your search. Runs: gam print groups [query …] fields …");
        _query.OnEnter(async () => await LoadGroupsAsync());

        var toolbar = Ui.Wrap(
            Ui.Margin(_query, 0, 0, 6, 6), _load,
            Ui.Button("New group…", async (_, _) => await CreateGroupAsync(), tip: "Creates a group. Runs: gam create group <email> name … description …"),
            Ui.Button("Group details", async (_, _) => await GroupDetailsAsync(), tip: "Shows the group's settings. Runs: gam info group <email>"),
            Ui.Button("Delete group…", async (_, _) => await DeleteGroupAsync(), "DangerButton", tip: "Deletes the selected groups. Runs: gam delete group <email>"));

        _groups.SelectionChanged += async (_, _) => await OnGroupSelected();

        var memberBar = Ui.Wrap(
            Ui.Button("Add members…", async (_, _) => await AddMembersAsync(), "PrimaryButton", tip: "Adds people to this group. Runs: gam update group <group> add <role> users <emails>"),
            Ui.Button("Change role…", async (_, _) => await ChangeRoleAsync(), tip: "Makes the selected members members, managers or owners. Runs: gam update group <group> update <role> users <emails>"),
            Ui.Button("Remove", async (_, _) => await RemoveMembersAsync(), "DangerButton", tip: "Removes the selected members. Runs: gam update group <group> remove users <emails>"),
            Ui.Button("Refresh", async (_, _) => await LoadMembersAsync(), tip: "Reloads the member list. Runs: gam print group-members group <group>"));

        var right = Ui.Grid("auto,auto,*").Add(
            _groupTitle.At(0),
            Ui.Margin(memberBar, 0, 8, 0, 0).At(1),
            _members.At(2));

        var body = Ui.Grid("*", "*,*");
        body.Add(_groups.At(0, 0), Ui.Margin(right, 16, 0, 0, 0).At(0, 1));

        Content = Ui.Grid("auto,auto,*").Add(
            Header("Groups", "Browse groups and manage who belongs to them."),
            toolbar.At(1),
            Ui.Margin(body, 0, 6, 0, 0).At(2));
    }

    protected override void OnBusyChanged(bool busy) => _load.IsEnabled = !busy;

    private async Task LoadGroupsAsync()
    {
        var args = new List<string> { "print", "groups" };
        if (_query.Value.Length > 0) { args.Add("query"); args.Add(_query.Value); }
        args.Add("fields");
        args.Add("email,name,description,directmemberscount");
        await RunAsync(new GamCommand(args), _groups);
    }

    private async Task OnGroupSelected()
    {
        var group = _groups.FirstSelected(GroupEmailCols);
        if (group == null || _groups.SelectedRows.Count != 1 || group == _currentGroup) return;
        _currentGroup = group;
        await LoadMembersAsync();
    }

    private async Task LoadMembersAsync()
    {
        if (_currentGroup == null) { _members.ShowEmpty(); return; }
        _groupTitle.Text = "Members of " + _currentGroup;
        int version = ++_memberLoadVersion;
        var cmd = GamCommand.Of("print", "group-members", "group", _currentGroup, "fields", "email,role,type,status");
        _members.ShowRunning(cmd.Display);
        try
        {
            var r = await Runner.RunAsync(cmd);
            if (version == _memberLoadVersion) _members.ShowResult(r, true); // ignore stale loads
        }
        catch (Exception ex)
        {
            _members.ShowText(ex.Message, true);
        }
    }

    private string? CurrentGroupOrExplain()
    {
        if (_currentGroup == null) Dialogs.Info(OwnerWindow, "Select a group first.");
        return _currentGroup;
    }

    private async Task CreateGroupAsync()
    {
        var v = FormDialog.Ask(OwnerWindow, "New group", "Creates the group with GAM's create group command.", "Create group",
            new Field("email", "Group email", Required: true),
            new Field("name", "Name"),
            new Field("description", "Description", Kind: FieldKind.MultiLine));
        if (v == null) return;
        var args = new List<string> { "create", "group", v["email"] };
        if (v["name"].Length > 0) { args.Add("name"); args.Add(v["name"]); }
        if (v["description"].Length > 0) { args.Add("description"); args.Add(v["description"]); }
        var r = await RunAsync(new GamCommand(args), confirm: $"Create {v["email"]}?");
        if (r == null) return;
        if (r.Success) await LoadGroupsAsync();
        else Dialogs.ShowResult(OwnerWindow, "Create group failed", r);
    }

    private async Task GroupDetailsAsync()
    {
        var g = _groups.FirstSelected(GroupEmailCols) ?? CurrentGroupOrExplain();
        if (g != null) await ShowTextOf(GamCommand.Of("info", "group", g), "Group: " + g);
    }

    private async Task DeleteGroupAsync()
    {
        var groups = RequireSelection(_groups, "groups", GroupEmailCols);
        if (groups.Count == 0) return;
        var cmds = groups.Select(g => GamCommand.Of("delete", "group", g)).ToList();
        if (await RunManyAsync(cmds, $"Delete {groups.Count} group(s)? This can't be undone.", showReport: false))
        {
            _currentGroup = null;
            _members.ShowEmpty();
            _groupTitle.Text = "Members";
            await LoadGroupsAsync();
        }
    }

    private async Task AddMembersAsync()
    {
        var g = CurrentGroupOrExplain();
        if (g == null) return;
        var v = FormDialog.Ask(OwnerWindow, "Add members", g, "Add",
            new Field("emails", "Email addresses", Kind: FieldKind.MultiLine, Required: true,
                Hint: "Separate with commas, spaces or new lines."),
            new Field("role", "Role", "member", FieldKind.Choice, new[] { "member", "manager", "owner" }));
        if (v == null) return;
        var emails = FormDialog.SplitList(v["emails"]);
        var r = await RunAsync(GamCommand.Of("update", "group", g, "add", v["role"], "users", string.Join(",", emails)),
            confirm: $"Add {emails.Count} member(s) to {g}?");
        await AfterMemberChange(r);
    }

    private async Task ChangeRoleAsync()
    {
        var g = CurrentGroupOrExplain();
        if (g == null) return;
        var emails = RequireSelection(_members, "members", MemberEmailCols);
        if (emails.Count == 0) return;
        var v = FormDialog.Ask(OwnerWindow, "Change role", $"{emails.Count} member(s) of {g}", "Change role",
            new Field("role", "New role", "member", FieldKind.Choice, new[] { "member", "manager", "owner" }));
        if (v == null) return;
        var r = await RunAsync(GamCommand.Of("update", "group", g, "update", v["role"], "users", string.Join(",", emails)),
            confirm: $"Make {emails.Count} member(s) {v["role"]}?");
        await AfterMemberChange(r);
    }

    private async Task RemoveMembersAsync()
    {
        var g = CurrentGroupOrExplain();
        if (g == null) return;
        var emails = RequireSelection(_members, "members", MemberEmailCols);
        if (emails.Count == 0) return;
        var r = await RunAsync(GamCommand.Of("update", "group", g, "remove", "users", string.Join(",", emails)),
            confirm: $"Remove {emails.Count} member(s) from {g}?");
        await AfterMemberChange(r);
    }

    private async Task AfterMemberChange(GamResult? r)
    {
        if (r == null) return;
        if (!r.Success) Dialogs.ShowResult(OwnerWindow, "GAM reported a problem", r);
        await LoadMembersAsync();
    }
}
