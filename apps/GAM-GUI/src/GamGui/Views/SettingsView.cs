using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GamGui.Core;
using Microsoft.Win32;

namespace GamGui.Views;

public sealed class SettingsView : ViewBase
{
    private const string SetupGuideUrl = "https://github.com/GAM-team/GAM/wiki/How-to-Install-GAM7";

    private readonly HintBox _gamPath = new("Leave empty to find gam.exe automatically");
    private readonly HintBox _configDir = new("Leave empty to use GAM's default config folder");
    private readonly HintBox _commandsFile = new("Leave empty to use the GamCommands.txt next to gam.exe");
    private readonly TextBlock _gamStatus = Ui.Text("", "Muted", 12.5);
    private readonly TextBlock _catalogStatus = Ui.Text("", "Muted", 12.5);
    private readonly ResultView _results = new("Use the checks above to confirm GAM is set up and authorized.");

    /// <summary>Raised after settings are saved so other screens can refresh.</summary>
    public static event Action? SettingsSaved;

    public SettingsView()
    {
        var s = AppServices.Settings;
        _gamPath.Text = s.GamPath;
        _configDir.Text = s.GamConfigDir;
        _commandsFile.Text = s.CommandsFile;

        var paths = Ui.Stack(
            Ui.Text("GAM program", "H2"),
            Ui.Margin(PathRow(_gamPath, Ui.Button("Browse…", (_, _) => BrowseGam()),
                Ui.Button("Find automatically", (_, _) => { _gamPath.Text = ""; UpdateStatus(); })), 0, 6, 0, 0),
            Ui.Margin(_gamStatus, 0, 0, 0, 14),

            Ui.Text("Config folder (GAMCFGDIR)", "H2"),
            Ui.Margin(Ui.Text("The folder with gam.cfg, oauth2.txt and oauth2service.json. Set this if you keep GAM's files somewhere other than its default location, or switch between domains.", "Muted", 12.5), 0, 2, 0, 0),
            Ui.Margin(PathRow(_configDir, Ui.Button("Browse…", (_, _) => BrowseFolder())), 0, 6, 0, 14),

            Ui.Text("Command reference", "H2"),
            Ui.Margin(Ui.Text("All commands is built from GAM's GamCommands.txt. Using the copy installed with gam.exe keeps the forms in step with your GAM version.", "Muted", 12.5), 0, 2, 0, 0),
            Ui.Margin(PathRow(_commandsFile, Ui.Button("Browse…", (_, _) => BrowseCommands())), 0, 6, 0, 0),
            Ui.Margin(_catalogStatus, 0, 0, 0, 12),

            Ui.Button("Save settings", (_, _) => Save(), "PrimaryButton"));

        var checks = Ui.Stack(
            Ui.Text("Check your setup", "H2"),
            Ui.Margin(Ui.Wrap(
                Ui.Button("GAM version", async (_, _) => await RunAsync(GamCommand.Of("version"), _results)),
                Ui.Button("Check connection", async (_, _) => await RunAsync(GamCommand.Of("checkconnection"), _results)),
                Ui.Button("Admin authorization", async (_, _) => await RunAsync(GamCommand.Of("oauth", "info"), _results)),
                Ui.Button("Domain info", async (_, _) => await RunAsync(GamCommand.Of("info", "domain"), _results)),
                Ui.Button("Show gam.cfg values", async (_, _) => await RunAsync(GamCommand.Of("config", "verify"), _results))), 0, 6, 0, 10),

            Ui.Text("First-time setup", "H2"),
            Ui.Margin(Ui.Text("These steps ask questions and open your browser, so they run in a console window. Do them in order.", "Muted", 12.5), 0, 2, 0, 0),
            Ui.Margin(Ui.Wrap(
                Ui.Button("1. Create the Google Cloud project", (_, _) => Terminal("create", "project")),
                Ui.Button("2. Authorize admin access", (_, _) => Terminal("oauth", "create")),
                Ui.Button("3. Check the service account…", (_, _) => CheckServiceAccount()),
                Ui.Button("Setup guide", (_, _) => OpenUrl(SetupGuideUrl))), 0, 6, 0, 0));

        var top = Ui.Grid("auto", "*,*");
        top.Add(Ui.Panel(paths).At(0, 0), Ui.Margin(Ui.Panel(checks), 14, 0, 0, 0).At(0, 1));

        Content = Ui.Grid("auto,auto,*").Add(
            Header("Settings", "Where GAM lives and how it's authorized. GAM Workbench runs your installed gam.exe for every action."),
            top.At(1),
            Ui.Margin(_results, 0, 14, 0, 0).At(2));

        _gamPath.Box.TextChanged += (_, _) => UpdateStatus();
        Loaded += (_, _) => UpdateStatus();
    }

    private static DockPanel PathRow(HintBox box, params Button[] buttons)
    {
        var row = new DockPanel { LastChildFill = true };
        var right = Ui.Row(buttons.Cast<UIElement>().ToArray());
        right.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(Ui.Margin(box, 0, 0, 0, 6));
        return row;
    }

    private void UpdateStatus()
    {
        var found = GamLocator.Find(_gamPath.Value);
        if (found != null)
        {
            _gamStatus.Text = "Using " + found;
            _gamStatus.Foreground = Ui.Brush("MutedBrush");
        }
        else
        {
            _gamStatus.Text = _gamPath.Value.Length > 0
                ? "No gam.exe at that path."
                : "gam.exe wasn't found. Install GAM7 (default folder C:\\GAM7) or browse to gam.exe.";
            _gamStatus.Foreground = Ui.Brush("DangerBrush");
        }
        try
        {
            var cat = AppServices.Catalog;
            _catalogStatus.Text = $"Using {cat.Source}: {cat.Commands.Count:N0} commands";
        }
        catch (Exception ex)
        {
            _catalogStatus.Text = "Couldn't read the command reference: " + ex.Message;
        }
    }

    private void Save()
    {
        var s = AppServices.Settings;
        s.GamPath = _gamPath.Value;
        s.GamConfigDir = _configDir.Value;
        s.CommandsFile = _commandsFile.Value;
        AppServices.SaveSettings();
        AppServices.ReloadCatalog();
        UpdateStatus();
        SettingsSaved?.Invoke();
    }

    private void BrowseGam()
    {
        var dlg = new OpenFileDialog { Filter = "GAM (gam.exe)|gam.exe|Programs (*.exe)|*.exe", Title = "Find gam.exe" };
        if (ShowDialog(dlg)) _gamPath.Text = dlg.FileName;
    }

    private void BrowseCommands()
    {
        var dlg = new OpenFileDialog { Filter = "GamCommands.txt|GamCommands.txt|Text files (*.txt)|*.txt", Title = "Find GamCommands.txt" };
        if (ShowDialog(dlg)) _commandsFile.Text = dlg.FileName;
    }

    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose GAM's config folder" };
        var owner = OwnerWindow;
        var ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
        if (ok == true) _configDir.Text = dlg.FolderName;
    }

    private bool ShowDialog(OpenFileDialog dlg)
    {
        var owner = OwnerWindow;
        return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true;
    }

    private void Terminal(params string[] args)
    {
        try { Runner.OpenInTerminal(new GamCommand(args)); }
        catch (Exception ex) { Dialogs.Error(OwnerWindow, ex.Message); }
    }

    private void CheckServiceAccount()
    {
        var v = FormDialog.Ask(OwnerWindow, "Check the service account",
            "Confirms that domain-wide delegation is set up for the Drive and Gmail features. GAM lists each scope and tells you how to fix any that fail.",
            "Check", new Field("user", "A user in your domain", Required: true));
        if (v != null) Terminal("user", v["user"], "check", "serviceaccount");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser registered */ }
    }
}
