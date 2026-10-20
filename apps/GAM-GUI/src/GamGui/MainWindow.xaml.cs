using System.Text;
using System.Windows;
using System.Windows.Controls;
using GamGui.Core;
using GamGui.Views;

namespace GamGui;

public partial class MainWindow : Window
{
    private sealed record Screen(string Section, string Title, Func<UserControl> Create);

    // Grouped the way an admin thinks about the work: start, manage things, do a job, go deeper.
    private readonly List<Screen> _screens = new()
    {
        new("", "Start", () => new HomeView()),
        new("Manage", "Users", () => new UsersView()),
        new("Manage", "Groups", () => new GroupsView()),
        new("Manage", "Org units", () => new OrgUnitsView()),
        new("Manage", "Devices", () => new DevicesView()),
        new("Manage", "Drive & Gmail", () => new UserDataView()),
        new("Tasks", "Quick tasks", () => new TasksView()),
        new("Advanced", "All commands", () => new CommandsView()),
        new("Advanced", "Console", () => new ConsoleView()),
        new("", "Settings", () => new SettingsView()),
    };

    private readonly Dictionary<string, UserControl> _created = new();
    private readonly List<string> _history = new();
    private string _lastDisplay = "";
    private int _running;

    public MainWindow()
    {
        InitializeComponent();

        string? section = null;
        foreach (var s in _screens)
        {
            if (s.Section != section && s.Section.Length > 0)
            {
                Nav.Items.Add(new ListBoxItem
                {
                    Style = (Style)FindResource("NavHeader"),
                    Content = new TextBlock
                    {
                        Text = s.Section.ToUpperInvariant(),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Ui.Brush("StripMutedBrush"),
                    },
                });
            }
            else if (s.Section.Length == 0 && section is { Length: > 0 })
            {
                Nav.Items.Add(new ListBoxItem { Style = (Style)FindResource("NavHeader"), Content = new Border { Height = 10 } });
            }
            section = s.Section;
            Nav.Items.Add(new ListBoxItem { Content = s.Title, Tag = s.Title });
        }

        AppServices.Runner.Started += cmd => Dispatcher.InvokeAsync(() => OnStarted(cmd));
        AppServices.Runner.Finished += (cmd, r) => Dispatcher.InvokeAsync(() => OnFinished(cmd, r));
        AppServices.OpenInConsoleRequested += line =>
        {
            ShowScreen("Console");
            (GetScreen("Console") as ConsoleView)?.SetCommand(line);
        };
        SettingsView.SettingsSaved += () => _ = RefreshGamInfoAsync();
        AppServices.NavigateRequested += screen => ShowScreen(screen);

        Loaded += async (_, _) =>
        {
            ShowScreen("Start");
            await RefreshGamInfoAsync();
        };
    }

    private UserControl GetScreen(string title)
    {
        if (!_created.TryGetValue(title, out var view))
        {
            view = _screens.First(s => s.Title == title).Create();
            _created[title] = view;
        }
        return view;
    }

    private void ShowScreen(string title)
    {
        var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == title);
        if (item != null && !item.IsSelected) item.IsSelected = true; // triggers Nav_SelectionChanged
        else Host.Content = GetScreen(title);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string title })
            Host.Content = GetScreen(title);
    }

    private async Task RefreshGamInfoAsync()
    {
        var s = AppServices.Settings;
        DomainText.Text = s.GamConfigDir.Length > 0 ? "Config folder:\n" + s.GamConfigDir : "";

        var path = AppServices.Runner.ResolveGamPath();
        if (path == null)
        {
            GamVersionText.Text = "gam.exe not found — see Settings";
            return;
        }
        try
        {
            var r = await AppServices.Runner.RunAsync(GamCommand.Of("version", "simple"));
            var v = (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            GamVersionText.Text = r.Success && v.Length > 0 && v.Length < 40 ? "GAM " + v : "GAM at " + path;
        }
        catch
        {
            GamVersionText.Text = "GAM at " + path;
        }
    }

    private void OnStarted(GamCommand cmd)
    {
        _running++;
        _lastDisplay = cmd.Display;
        StripCommand.Text = cmd.Display;
        StripCommand.ToolTip = cmd.Display;
        StripStatus.Text = _running > 1 ? $"Running ({_running})…" : "Running…";
        StripStatus.Foreground = Ui.Brush("StripTextBrush");
    }

    private void OnFinished(GamCommand cmd, GamResult r)
    {
        _running = Math.Max(0, _running - 1);
        var outcome = r.Cancelled ? "stopped" : r.ExitCode == 0 ? "ok" : $"exit {r.ExitCode}";
        _history.Add($"{DateTime.Now:HH:mm:ss}  {outcome,-8} {r.Elapsed.TotalSeconds,6:0.0}s  {cmd.Display}");
        if (_running > 0) return;
        StripCommand.Text = cmd.Display;
        _lastDisplay = cmd.Display;
        StripStatus.Text = r.Cancelled ? "Stopped"
            : r.ExitCode == 0 ? $"Finished in {r.Elapsed.TotalSeconds:0.0} s"
            : $"GAM exited with code {r.ExitCode}";
        StripStatus.Foreground = r.ExitCode == 0 || r.Cancelled ? Ui.Brush("StripMutedBrush") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x8A, 0x80));
    }

    private void StripCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDisplay.Length > 0) Ui.Copy(_lastDisplay);
    }

    private void StripHistory_Click(object sender, RoutedEventArgs e)
    {
        var text = _history.Count == 0
            ? "No commands have run yet."
            : new StringBuilder().AppendJoin("\n", Enumerable.Reverse(_history)).ToString();
        Dialogs.ShowText(this, "Command history (this session)", text);
    }
}
