using System.Data;
using System.Windows;
using System.Windows.Controls;
using GamGui.Core;

namespace GamGui.Views;

public sealed class OrgUnitsView : ViewBase
{
    private readonly TreeView _tree = new() { BorderBrush = Ui.Brush("LineBrush"), Padding = new Thickness(4) };
    private readonly TextBlock _path = Ui.Text("Select an org unit", "H2");
    private readonly TextBlock _description = Ui.Text("", "Muted");
    private readonly ResultView _users = new("Choose Show users to list the accounts in this org unit.");
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _load;

    public OrgUnitsView()
    {
        _load = Ui.Button("Load org units", async (_, _) => await LoadAsync(), "PrimaryButton", tip: "Reads your org structure. Runs: gam print orgs allfields");
        _tree.SelectedItemChanged += (_, _) => OnSelected();

        var actions = Ui.Wrap(
            Ui.Button("Show users", async (_, _) => await ShowUsersAsync(), "PrimaryButton", tip: "Lists users in the selected org unit. Runs: gam print users limittoou <path>"),
            Ui.Button("New org unit here…", async (_, _) => await CreateAsync(), tip: "Creates a child org unit. Runs: gam create org <path> [description …]"),
            Ui.Button("Rename…", async (_, _) => await RenameAsync(), tip: "Renames the selected org unit. Runs: gam update org <path> name <new name>"),
            Ui.Button("Edit description…", async (_, _) => await DescribeAsync(), tip: "Runs: gam update org <path> description …"),
            Ui.Button("Move users here…", async (_, _) => await MoveUsersAsync(), tip: "Moves users into the selected org unit. Runs: gam update org <path> add users <emails>"),
            Ui.Button("Details", async (_, _) => await DetailsAsync(), tip: "Runs: gam info org <path> nousers"),
            Ui.Button("Delete…", async (_, _) => await DeleteAsync(), "DangerButton", tip: "Deletes an empty org unit. Runs: gam delete org <path>"));

        var right = Ui.Grid("auto,auto,auto,*").Add(
            _path.At(0),
            Ui.Margin(_description, 0, 2, 0, 8).At(1),
            actions.At(2),
            Ui.Margin(_users, 0, 6, 0, 0).At(3));

        var body = Ui.Grid("*", "300,*");
        body.Add(_tree.At(0, 0), Ui.Margin(right, 16, 0, 0, 0).At(0, 1));

        Content = Ui.Grid("auto,auto,*").Add(
            Header("Org units", "Your organization's structure. Select an org unit to manage it and its users."),
            Ui.Wrap(_load).At(1),
            Ui.Margin(body, 0, 6, 0, 0).At(2));
    }

    protected override void OnBusyChanged(bool busy) => _load.IsEnabled = !busy;

    private string? SelectedPath => (_tree.SelectedItem as TreeViewItem)?.Tag as string;

    private async Task LoadAsync()
    {
        var cmd = GamCommand.Of("print", "orgs", "allfields");
        var r = await RunAsync(cmd);
        if (r == null) return;
        var table = CsvTable.TryParse(r.StdOut);
        if (!r.Success || table == null)
        {
            Dialogs.ShowResult(OwnerWindow, "Couldn't load org units", r);
            return;
        }
        BuildTree(table);
    }

    private void BuildTree(DataTable table)
    {
        var previous = SelectedPath;
        _tree.Items.Clear();
        _descriptions.Clear();
        var pathCol = CsvTable.FindColumn(table, "orgUnitPath", "path");
        var descCol = CsvTable.FindColumn(table, "description");
        if (pathCol == null) return;

        var root = MakeItem("/", "/ (top level)");
        _tree.Items.Add(root);
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { ["/"] = root };

        var paths = table.Rows.Cast<DataRow>()
            .Select(r => (path: r[pathCol.ColumnName]?.ToString() ?? "", desc: descCol == null ? "" : r[descCol.ColumnName]?.ToString() ?? ""))
            .Where(p => p.path.StartsWith('/'))
            .OrderBy(p => p.path, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, desc) in paths)
        {
            _descriptions[path] = desc;
            var parent = GetOrCreate(nodes, ParentOf(path));
            if (!nodes.ContainsKey(path))
            {
                var item = MakeItem(path, NameOf(path));
                parent.Items.Add(item);
                nodes[path] = item;
            }
        }
        root.IsExpanded = true;
        if (previous != null && nodes.TryGetValue(previous, out var sel))
        {
            for (var p = sel.Parent as TreeViewItem; p != null; p = p.Parent as TreeViewItem) p.IsExpanded = true;
            sel.IsSelected = true;
        }
    }

    private TreeViewItem GetOrCreate(Dictionary<string, TreeViewItem> nodes, string path)
    {
        if (nodes.TryGetValue(path, out var existing)) return existing;
        var parent = GetOrCreate(nodes, ParentOf(path));
        var item = MakeItem(path, NameOf(path));
        parent.Items.Add(item);
        nodes[path] = item;
        return item;
    }

    private static TreeViewItem MakeItem(string path, string header) =>
        new() { Header = header, Tag = path, Padding = new Thickness(2, 3, 2, 3) };

    private static string ParentOf(string path)
    {
        var i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string NameOf(string path) => path.TrimEnd('/').Split('/').Last();

    private void OnSelected()
    {
        var p = SelectedPath;
        _path.Text = p ?? "Select an org unit";
        _description.Text = p != null && _descriptions.TryGetValue(p, out var d) && d.Length > 0 ? d : "";
        _users.ShowEmpty();
    }

    private string? RequirePath()
    {
        var p = SelectedPath;
        if (p == null) Dialogs.Info(OwnerWindow, _tree.Items.Count == 0 ? "Load org units first." : "Select an org unit first.");
        return p;
    }

    private async Task ShowUsersAsync()
    {
        var p = RequirePath();
        if (p == null) return;
        await RunAsync(GamCommand.Of("print", "users", "limittoou", p, "fields", "primaryemail,name,suspended,orgunitpath"), _users);
    }

    private async Task DetailsAsync()
    {
        var p = RequirePath();
        if (p != null) await ShowTextOf(GamCommand.Of("info", "org", p, "nousers"), "Org unit: " + p);
    }

    private async Task CreateAsync()
    {
        var parent = SelectedPath ?? "/";
        var v = FormDialog.Ask(OwnerWindow, "New org unit", "Inside " + parent, "Create",
            new Field("name", "Name", Required: true),
            new Field("description", "Description"));
        if (v == null) return;
        var path = (parent == "/" ? "" : parent.TrimEnd('/')) + "/" + v["name"];
        var args = new List<string> { "create", "org", path };
        if (v["description"].Length > 0) { args.Add("description"); args.Add(v["description"]); }
        await AfterChange(await RunAsync(new GamCommand(args), confirm: $"Create {path}?"));
    }

    private async Task RenameAsync()
    {
        var p = RequirePath();
        if (p == null || p == "/") return;
        var v = FormDialog.Ask(OwnerWindow, "Rename org unit", p, "Rename", new Field("name", "New name", NameOf(p), Required: true));
        if (v == null) return;
        await AfterChange(await RunAsync(GamCommand.Of("update", "org", p, "name", v["name"]), confirm: $"Rename {p}?"));
    }

    private async Task DescribeAsync()
    {
        var p = RequirePath();
        if (p == null || p == "/") return;
        _descriptions.TryGetValue(p, out var current);
        var v = FormDialog.Ask(OwnerWindow, "Edit description", p, "Save", new Field("d", "Description", current ?? ""));
        if (v == null) return;
        await AfterChange(await RunAsync(GamCommand.Of("update", "org", p, "description", v["d"]), confirm: $"Update {p}?"));
    }

    private async Task MoveUsersAsync()
    {
        var p = RequirePath();
        if (p == null) return;
        var v = FormDialog.Ask(OwnerWindow, "Move users here", p, "Move",
            new Field("emails", "User email addresses", Kind: FieldKind.MultiLine, Required: true,
                Hint: "Separate with commas, spaces or new lines."));
        if (v == null) return;
        var emails = FormDialog.SplitList(v["emails"]);
        var r = await RunAsync(GamCommand.Of("update", "org", p, "add", "users", string.Join(",", emails)),
            confirm: $"Move {emails.Count} user(s) to {p}?");
        if (r == null) return;
        if (!r.Success) Dialogs.ShowResult(OwnerWindow, "GAM reported a problem", r);
        else await ShowUsersAsync();
    }

    private async Task DeleteAsync()
    {
        var p = RequirePath();
        if (p == null || p == "/") return;
        await AfterChange(await RunAsync(GamCommand.Of("delete", "org", p),
            confirm: $"Delete {p}? It must not contain users, devices or child org units."));
    }

    private async Task AfterChange(GamResult? r)
    {
        if (r == null) return;
        if (r.Success) await LoadAsync();
        else Dialogs.ShowResult(OwnerWindow, "GAM reported a problem", r);
    }
}
