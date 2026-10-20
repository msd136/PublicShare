using System.Windows;
using System.Windows.Controls;
using GamGui.Core;

namespace GamGui.Views;

/// <summary>Drive and Gmail data for one user (requires service account domain-wide delegation).</summary>
public sealed class UserDataView : ViewBase
{
    private sealed record Report(string Name, string? QueryHint, Func<string, string, List<string>> Build);

    private static readonly Report[] Reports =
    {
        new("Drive files", "Drive query, e.g. name contains 'budget'", (u, q) =>
        {
            var a = new List<string> { "user", u, "print", "filelist" };
            if (q.Length > 0) { a.Add("query"); a.Add(q); }
            a.AddRange(new[] { "fields", "id,name,mimetype,modifiedtime,size" });
            return a;
        }),
        new("Shared drives", null, (u, _) => new() { "user", u, "print", "shareddrives" }),
        new("Gmail messages", "Gmail search, e.g. from:boss newer_than:7d", (u, q) =>
        {
            var a = new List<string> { "user", u, "print", "messages" };
            if (q.Length > 0) { a.Add("query"); a.Add(q); }
            a.AddRange(new[] { "max_to_print", "200" });
            return a;
        }),
        new("Gmail labels", null, (u, _) => new() { "user", u, "print", "labels" }),
        new("Gmail filters", null, (u, _) => new() { "user", u, "print", "filters" }),
        new("Send-as addresses", null, (u, _) => new() { "user", u, "print", "sendas" }),
        new("Delegates", null, (u, _) => new() { "user", u, "print", "delegates", "shownames" }),
        new("Forwarding", null, (u, _) => new() { "user", u, "show", "forward" }),
        new("Vacation responder", null, (u, _) => new() { "user", u, "show", "vacation" }),
        new("Signature", null, (u, _) => new() { "user", u, "show", "signature" }),
    };

    private readonly HintBox _user = new("user@yourdomain.com", 260);
    private readonly ComboBox _report = Ui.Combo(Reports.Select(r => r.Name), 0, 180);
    private readonly HintBox _query = new("", 320);
    private readonly ResultView _results = new("Enter a user's email address, pick what to view, then choose Show.");
    private readonly Button _show;

    public UserDataView()
    {
        _show = Ui.Button("Show", async (_, _) => await ShowAsync(), "PrimaryButton", tip: "Runs the GAM command for the item you picked, for this user");
        _user.OnEnter(async () => await ShowAsync());
        _query.OnEnter(async () => await ShowAsync());
        _report.SelectionChanged += (_, _) => UpdateQueryBox();
        UpdateQueryBox();

        var toolbar = Ui.Wrap(
            Ui.Margin(_user, 0, 0, 6, 6), Ui.Margin(_report, 0, 0, 6, 6), Ui.Margin(_query, 0, 0, 6, 6), _show);

        var actions = Ui.Wrap(
            Ui.Text("Change settings:", "Muted"),
            Ui.Margin(Ui.Button("Vacation responder…", async (_, _) => await VacationAsync(), tip: "Runs: gam user <email> vacation on|off subject … message …"), 8, 0, 6, 6),
            Ui.Button("Forwarding…", async (_, _) => await ForwardingAsync(), tip: "Runs: gam user <email> forward on <keep|archive|…> <address>, or forward off"),
            Ui.Button("Add delegate…", async (_, _) => await DelegateAsync(add: true), tip: "Gives someone access to this mailbox. Runs: gam user <email> add delegate <person>"),
            Ui.Button("Remove delegate…", async (_, _) => await DelegateAsync(add: false), tip: "Runs: gam user <email> delete delegate <person>"),
            Ui.Button("Transfer Drive files…", async (_, _) => await TransferAsync(), tip: "Moves file ownership to another user. Runs: gam create datatransfer <user> drive <new owner> all|shared"));
        ((TextBlock)actions.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        ((TextBlock)actions.Children[0]).Margin = new Thickness(0, 0, 0, 6);

        Content = Ui.Grid("auto,auto,auto,*").Add(
            Header("Drive & Gmail", "Look into a user's Drive and Gmail. Needs GAM's service account with domain-wide delegation."),
            toolbar.At(1),
            actions.At(2),
            Ui.Margin(_results, 0, 6, 0, 0).At(3));
    }

    protected override void OnBusyChanged(bool busy) => _show.IsEnabled = !busy;

    private Report Current => Reports[Math.Max(0, _report.SelectedIndex)];

    private void UpdateQueryBox()
    {
        var hint = Current.QueryHint;
        _query.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
        if (hint != null) _query.Hint = hint;
    }

    private string? RequireUser()
    {
        var u = _user.Value;
        if (u.Length == 0) { Dialogs.Info(OwnerWindow, "Enter the user's email address first."); return null; }
        return u;
    }

    private async Task ShowAsync()
    {
        var u = RequireUser();
        if (u == null) return;
        var q = Current.QueryHint == null ? "" : _query.Value;
        await RunAsync(new GamCommand(Current.Build(u, q)), _results);
    }

    private async Task VacationAsync()
    {
        var u = RequireUser();
        if (u == null) return;
        var v = FormDialog.Ask(OwnerWindow, "Vacation responder", u, "Save",
            new Field("on", "Turn the vacation responder on", "true", FieldKind.Check),
            new Field("subject", "Subject"),
            new Field("message", "Message", Kind: FieldKind.MultiLine),
            new Field("domainonly", "Only reply to people in my organization", "false", FieldKind.Check));
        if (v == null) return;
        var args = new List<string> { "user", u, "vacation", v["on"] == "true" ? "on" : "off" };
        if (v["on"] == "true")
        {
            if (v["subject"].Length > 0) { args.Add("subject"); args.Add(v["subject"]); }
            if (v["message"].Length > 0) { args.Add("message"); args.Add(v["message"]); }
            args.Add("domainonly"); args.Add(v["domainonly"] == "true" ? "true" : "false");
        }
        await ShowOutcome(await RunAsync(new GamCommand(args), confirm: $"Update the vacation responder for {u}?"));
    }

    private async Task ForwardingAsync()
    {
        var u = RequireUser();
        if (u == null) return;
        var v = FormDialog.Ask(OwnerWindow, "Forwarding", u, "Save",
            new Field("on", "Forward incoming mail", "true", FieldKind.Check),
            new Field("to", "Forward to", Hint: "The address must already be a verified forwarding address for this user."),
            new Field("disposition", "Then, in this mailbox", "keep", FieldKind.Choice,
                new[] { "keep", "archive", "markread", "trash", "delete" }));
        if (v == null) return;
        GamCommand cmd;
        if (v["on"] == "true")
        {
            if (v["to"].Length == 0) { Dialogs.Info(OwnerWindow, "Enter the address to forward to."); return; }
            cmd = GamCommand.Of("user", u, "forward", "on", v["disposition"], v["to"]);
        }
        else cmd = GamCommand.Of("user", u, "forward", "off");
        await ShowOutcome(await RunAsync(cmd, confirm: $"Update forwarding for {u}?"));
    }

    private async Task DelegateAsync(bool add)
    {
        var u = RequireUser();
        if (u == null) return;
        var v = FormDialog.Ask(OwnerWindow, add ? "Add delegate" : "Remove delegate",
            add ? $"The delegate will be able to read, send and delete mail in {u}'s mailbox." : u,
            add ? "Add delegate" : "Remove delegate",
            new Field("who", "Delegate email", Required: true));
        if (v == null) return;
        var cmd = GamCommand.Of("user", u, add ? "add" : "delete", "delegate", v["who"]);
        await ShowOutcome(await RunAsync(cmd, confirm: add ? $"Give {v["who"]} access to {u}'s mailbox?" : $"Remove {v["who"]} as a delegate?"));
    }

    private async Task TransferAsync()
    {
        var u = RequireUser();
        if (u == null) return;
        var v = FormDialog.Ask(OwnerWindow, "Transfer Drive files",
            "Moves ownership of all of this user's Drive files to another user, using Google's data transfer service.",
            "Transfer",
            new Field("to", "New owner email", Required: true),
            new Field("private", "Include private files", "true", FieldKind.Check));
        if (v == null) return;
        var cmd = GamCommand.Of("create", "datatransfer", u, "drive", v["to"], v["private"] == "true" ? "all" : "shared");
        await ShowOutcome(await RunAsync(cmd, confirm: $"Transfer {u}'s Drive files to {v["to"]}?"));
    }

    private Task ShowOutcome(GamResult? r)
    {
        if (r != null)
            Dialogs.ShowResult(OwnerWindow, r.Success ? "Done" : "GAM reported a problem", r);
        return Task.CompletedTask;
    }
}
