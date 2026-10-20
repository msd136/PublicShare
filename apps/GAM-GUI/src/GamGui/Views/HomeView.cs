using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GamGui.Core;

namespace GamGui.Views;

public sealed class HomeView : ViewBase
{
    private sealed class Check
    {
        public required TextBlock Dot { get; init; }
        public required TextBlock Detail { get; init; }
        public required WrapPanel Actions { get; init; }
    }

    private readonly Check _installed;
    private readonly Check _network;
    private readonly Check _auth;
    private readonly Check _directory;
    private readonly Check _serviceAccount;
    private readonly Button _runChecks;
    private bool _checkedOnce;

    public HomeView()
    {
        var checklist = new StackPanel();
        checklist.Children.Add(Ui.Text("Setup checklist", "H2"));
        checklist.Children.Add(Ui.Margin(Ui.Text("Confirms GAM is ready. Items that fail explain what's wrong and how to fix it.", "Muted", 12.5), 0, 2, 0, 10));
        _installed = AddCheck(checklist, "GAM is installed", "Looks for gam.exe on this computer.");
        _network = AddCheck(checklist, "GAM can reach Google", "Runs \"gam checkconnection\" to test each Google address GAM uses.");
        _auth = AddCheck(checklist, "GAM is authorized as an administrator", "Runs \"gam oauth info\" to read GAM's saved admin sign-in.");
        _directory = AddCheck(checklist, "Your directory can be read", "Runs \"gam info domain\", a harmless read that proves the whole path works.");
        _serviceAccount = AddCheck(checklist, "Drive and Gmail access (optional)",
            "Needed for the Drive & Gmail screen and some quick tasks. GAM tests it against a user you choose.");
        SetState(_serviceAccount, null, "Not checked. Choose Check to test it.");
        _serviceAccount.Actions.Children.Add(Ui.Button("Check…", (_, _) => CheckServiceAccount(),
            tip: "Opens a console window running \"gam user <email> check serviceaccount\""));

        _runChecks = Ui.Button("Run checks", async (_, _) => await RunChecksAsync(), "PrimaryButton",
            "Runs the checks above again");
        checklist.Children.Add(Ui.Margin(_runChecks, 0, 10, 0, 0));

        var tiles = new WrapPanel();
        tiles.Children.Add(Tile("Users", "Find, create, suspend and reset passwords for accounts."));
        tiles.Children.Add(Tile("Groups", "See who's in a group; add or remove members."));
        tiles.Children.Add(Tile("Org units", "Your organization's structure; move people between units."));
        tiles.Children.Add(Tile("Devices", "Chromebooks and phones: find, disable, wipe."));
        tiles.Children.Add(Tile("Drive & Gmail", "A user's files and mail settings, vacation replies, delegates."));
        tiles.Children.Add(Tile("Quick tasks", "Ready-made jobs: offboarding, inactive users, mailbox access and more."));

        var how = Ui.Stack(
            Ui.Text("How this app works", "H2"),
            Ui.Margin(Ui.Text("GAM Workbench is a front end for GAM, the command-line tool for Google Workspace. Each button runs GAM with the right options, so you get exactly what GAM would do, without typing commands."), 0, 4, 0, 6),
            Ui.Margin(Ui.Text("Anything that changes data first shows what will happen and the exact command, and waits for you to choose Run. The strip at the bottom of the window shows each command as it runs."), 0, 0, 0, 6),
            Ui.Text("If Google returns an error the app recognizes, you'll see what it means and how to fix it, often with a button that does the fix."));

        var left = Ui.Stack(Ui.Panel(checklist));
        var right = Ui.Stack(
            Ui.Text("What would you like to do?", "H2"),
            Ui.Margin(tiles, 0, 8, 0, 6),
            Ui.Panel(how));

        var body = Ui.Grid("auto", "420,*");
        body.Add(left.At(0, 0), Ui.Margin(right, 18, 0, 0, 0).At(0, 1));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Ui.Stack(
                Header("Start", "Welcome to GAM Workbench. Check that everything is set up, then pick what you'd like to do."),
                body),
        };

        Loaded += async (_, _) =>
        {
            if (_checkedOnce) return;
            _checkedOnce = true;
            await RunChecksAsync();
        };
    }

    private static Check AddCheck(StackPanel parent, string title, string description)
    {
        var dot = new TextBlock { Text = "●", FontSize = 15, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        var detail = Ui.Text(description, "Muted", 12.5);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var text = Ui.Stack(Ui.Text(title, "H2"), detail, actions);
        var row = Ui.Grid("auto", "auto,*");
        row.Add(dot.At(0, 0), text.At(0, 1));
        row.Margin = new Thickness(0, 0, 0, 10);
        parent.Children.Add(row);
        var check = new Check { Dot = dot, Detail = detail, Actions = actions };
        SetState(check, null, description);
        return check;
    }

    /// <param name="ok">true = passed, false = failed, null = not checked or running.</param>
    private static void SetState(Check c, bool? ok, string detail, bool running = false)
    {
        c.Dot.Foreground = running ? Ui.Brush("AccentBrush")
            : ok == true ? Ui.Brush("OkBrush")
            : ok == false ? Ui.Brush("DangerBrush")
            : Ui.Brush("LineBrush");
        c.Dot.ToolTip = running ? "Checking…" : ok == true ? "Passed" : ok == false ? "Needs attention" : "Not checked";
        c.Detail.Text = detail;
    }

    private Button Tile(string screen, string description)
    {
        var b = new Button
        {
            Width = 250,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 10, 10),
            Content = Ui.Stack(Ui.Text(screen, "H2"), Ui.Margin(Ui.Text(description, "Muted", 12.5), 0, 2, 0, 0)),
            ToolTip = "Open " + screen,
        };
        b.Click += (_, _) => AppServices.Navigate(screen);
        return b;
    }

    private async Task RunChecksAsync()
    {
        _runChecks.IsEnabled = false;
        try
        {
            foreach (var c in new[] { _installed, _network, _auth, _directory }) c.Actions.Children.Clear();

            var path = Runner.ResolveGamPath();
            if (path == null)
            {
                SetState(_installed, false, "gam.exe wasn't found. Install GAM7 (it goes to C:\\GAM7 by default), or tell the app where it is.");
                _installed.Actions.Children.Add(Ui.Button("Open Settings", (_, _) => AppServices.Navigate("Settings")));
                _installed.Actions.Children.Add(Ui.Button("How to install GAM7", (_, _) =>
                    Dialogs.OpenUrl("https://github.com/GAM-team/GAM/wiki/How-to-Install-GAM7")));
                foreach (var c in new[] { _network, _auth, _directory }) SetState(c, null, "Waiting for GAM to be installed.");
                return;
            }
            SetState(_installed, true, "Found " + path);

            await RunCheck(_network, GamCommand.Of("checkconnection"), "All of Google's addresses can be reached.");
            await RunCheck(_auth, GamCommand.Of("oauth", "info"), "GAM has a saved admin sign-in.",
                Ui.Button("Authorize GAM", (_, _) => Terminal("oauth", "create"),
                    tip: "Opens a console window running \"gam oauth create\", then your browser to sign in"));
            await RunCheck(_directory, GamCommand.Of("info", "domain"), "GAM can read your domain's settings.");
        }
        finally
        {
            _runChecks.IsEnabled = true;
        }
    }

    private async Task RunCheck(Check check, GamCommand cmd, string okText, Button? fixWhenFailed = null)
    {
        SetState(check, null, "Checking…", running: true);
        GamResult r;
        try { r = await Runner.RunAsync(cmd); }
        catch (Exception ex) { SetState(check, false, ex.Message); return; }

        if (r.Success)
        {
            SetState(check, true, okText);
            return;
        }
        var advice = ErrorAdvisor.Diagnose(r);
        SetState(check, false, advice != null ? advice.Title + ". " + advice.Explanation : "GAM reported a problem. Choose Details to see what it said.");
        check.Actions.Children.Add(Ui.Button("Details and fix", (_, _) => Dialogs.ShowResult(OwnerWindow, cmd.Display, r), "PrimaryButton"));
        if (fixWhenFailed != null && advice?.Fix == null) check.Actions.Children.Add(fixWhenFailed);
    }

    private void CheckServiceAccount()
    {
        var v = FormDialog.Ask(OwnerWindow, "Check Drive and Gmail access",
            "GAM tests each scope its service account needs, using the user you enter. For any that fail, it prints the Admin console link and the exact scopes to add.",
            "Open check", new Field("user", "A user in your domain", Required: true));
        if (v == null) return;
        Terminal("user", v["user"], "check", "serviceaccount");
        SetState(_serviceAccount, null, "Opened in a console window. Read GAM's results there.");
    }

    private void Terminal(params string[] args)
    {
        try { Runner.OpenInTerminal(new GamCommand(args)); }
        catch (Exception ex) { Dialogs.Error(OwnerWindow, ex.Message); }
    }
}
