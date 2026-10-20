using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamGui.Builder;
using GamGui.Core;

namespace GamGui.Views;

/// <summary>
/// Every GAM command, straight from GAM's own GamCommands.txt. Pick a command, fill in the
/// generated form, and the exact command line is built as you go.
/// </summary>
public sealed class CommandsView : ViewBase
{
    private readonly HintBox _search = new("Search commands, e.g. print users or vacation");
    private readonly TreeView _tree = new() { BorderBrush = Ui.Brush("LineBrush") };
    private readonly TextBlock _count = Ui.Text("", "Muted", 12);
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private readonly TextBlock _title = new() { Style = Ui.Style("H1"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _section = Ui.Text("", "Muted");
    private readonly TextBox _syntax = new() { Style = Ui.Style("MonoText"), MaxHeight = 150, BorderThickness = new Thickness(0) };
    private readonly TextBlock _notes = Ui.Text("", "Muted", 12.5);
    private readonly ScrollViewer _formHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 4, 8, 4) };

    private readonly TextBox _preview = new()
    {
        IsReadOnly = true,
        FontFamily = (FontFamily)Ui.Res("MonoFont"),
        Background = Ui.Brush("StripBrush"),
        Foreground = Ui.Brush("StripTextBrush"),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10, 7, 10, 7),
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _missing = new() { Foreground = Ui.Brush("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _run;
    private readonly Button _stop;
    private readonly ResultView _results = new("Run a command to see its output here.");

    private FormPart? _form;
    private List<string> _args = new();
    private List<string> _missingList = new();
    private CancellationTokenSource? _cts;
    private bool _treeBuilt;

    public CommandsView()
    {
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); BuildTree(); };
        _search.Box.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _tree.SelectedItemChanged += (_, _) =>
        {
            if ((_tree.SelectedItem as TreeViewItem)?.Tag is GamCommandInfo info) Select(info);
        };

        var left = Ui.Grid("auto,auto,*").Add(
            _search.At(0),
            Ui.Margin(_count, 2, 4, 0, 6).At(1),
            _tree.At(2));

        _run = Ui.Button("Run", async (_, _) => await RunCurrentAsync(), "PrimaryButton");
        _stop = Ui.Button("Stop", (_, _) => _cts?.Cancel());
        _stop.IsEnabled = false;
        var buttons = Ui.Wrap(
            _run, _stop,
            Ui.Button("Copy command", (_, _) => Ui.Copy(CurrentCommand().Display)),
            Ui.Button("Edit in console", (_, _) => AppServices.OpenInConsole(ArgUtil.Join(_args))),
            Ui.Button("Open in terminal", (_, _) => OpenTerminal(),
                tip: "Runs in a console window. Use this for commands that ask questions or open a browser."));
        buttons.Margin = new Thickness(0, 8, 0, 0);

        var previewPanel = Ui.Stack(_preview, _missing, buttons);

        var splitter = new GridSplitter
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Rows,
            Cursor = System.Windows.Input.Cursors.SizeNS,
        };

        // Command, form and preview share the top pane; the splitter sits between two star rows.
        var editor = Ui.Grid("auto,auto,auto,auto,*,auto").Add(
            _title.At(0),
            _section.At(1),
            Ui.Margin(new Border { BorderBrush = Ui.Brush("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = _syntax }, 0, 8, 0, 4).At(2),
            Ui.Margin(_notes, 0, 0, 0, 6).At(3),
            _formHost.At(4),
            Ui.Margin(previewPanel, 0, 6, 0, 0).At(5));

        var detail = Ui.Grid("3*,6,2*").Add(
            editor.At(0),
            splitter.At(1),
            _results.At(2));

        var body = Ui.Grid("*", "320,*");
        body.Add(left.At(0, 0), Ui.Margin(detail, 18, 0, 0, 0).At(0, 1));

        Content = Ui.Grid("auto,*").Add(
            Header("All commands", "Every GAM command, generated from GAM's own syntax reference. Hover over a value to see what it accepts."),
            body.At(1));

        _title.Text = "Pick a command";
        _section.Text = "Search or browse on the left. The form, the exact command line, and the output all appear here.";
        _syntax.Text = "";
        Loaded += (_, _) => { if (!_treeBuilt) BuildTree(); };
        SettingsView.SettingsSaved += () => { if (_treeBuilt) BuildTree(); };
    }

    protected override void OnBusyChanged(bool busy)
    {
        _run.IsEnabled = !busy;
        _stop.IsEnabled = busy;
    }

    private void BuildTree()
    {
        _treeBuilt = true;
        CommandCatalog cat;
        try { cat = AppServices.Catalog; }
        catch (Exception ex)
        {
            _count.Text = "Couldn't read GamCommands.txt: " + ex.Message;
            return;
        }
        var terms = _search.Value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = cat.Commands.Where(c => terms.All(t => c.SearchText.Contains(t))).ToList();
        _tree.Items.Clear();
        foreach (var group in matches.GroupBy(c => c.Section))
        {
            var sec = new TreeViewItem
            {
                Header = $"{group.Key}  ({group.Count()})",
                IsExpanded = terms.Length > 0 && matches.Count <= 150,
                FontWeight = FontWeights.SemiBold,
            };
            foreach (var c in group)
            {
                sec.Items.Add(new TreeViewItem
                {
                    Header = new TextBlock { Text = c.Title, FontFamily = (FontFamily)Ui.Res("MonoFont"), FontSize = 12, FontWeight = FontWeights.Normal },
                    Tag = c,
                    ToolTip = new TextBlock { Text = c.Syntax, FontFamily = (FontFamily)Ui.Res("MonoFont"), MaxWidth = 700, TextWrapping = TextWrapping.Wrap },
                });
            }
            _tree.Items.Add(sec);
        }
        _count.Text = $"{matches.Count:N0} of {cat.Commands.Count:N0} commands · {cat.Source}";
    }

    private void Select(GamCommandInfo info)
    {
        _title.Text = info.Title;
        _section.Text = info.Section + $"  ·  GamCommands.txt line {info.LineNumber}";
        _syntax.Text = info.Syntax;
        _notes.Text = info.Notes;
        _notes.Visibility = info.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            _form = FormBuilder.Build(info.Node, AppServices.Catalog);
            _form.Changed += (_, _) => UpdatePreview();
            _formHost.Content = _form.View;
        }
        catch (Exception ex)
        {
            _form = null;
            _formHost.Content = Ui.Text("This command's syntax couldn't be turned into a form (" + ex.Message +
                                        "). Use Edit in console to type it directly.", "Muted");
        }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var args = new List<string>();
        var missing = new List<string>();
        _form?.Emit(args, missing);
        if (args.Count > 0 && args[0] == "gam") args.RemoveAt(0);
        _args = args;
        _missingList = missing;
        _preview.Text = CurrentCommand().Display;

        var notes = new List<string>();
        if (missing.Count > 0) notes.Add("Still needed: " + string.Join(", ", missing.Distinct()));
        if (LooksInteractive(args)) notes.Add("This command may ask questions or open a browser. Use Open in terminal.");
        _missing.Text = string.Join("\n", notes);
        _missing.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private GamCommand CurrentCommand() => new(_args);

    private static bool LooksInteractive(List<string> args)
    {
        var a = args.Select(x => x.ToLowerInvariant()).ToList();
        if (a.Count == 0) return false;
        return a[0] == "oauth" || a.Contains("project") || a.Contains("svcacct") || a.Contains("sakey") ||
               (a[0] == "setup") || (a.Count > 1 && a[0] == "check" && a[1] == "svcacct");
    }

    private async Task RunCurrentAsync()
    {
        if (_form == null || _args.Count == 0)
        {
            Dialogs.Info(OwnerWindow, "Pick a command first.");
            return;
        }
        var cmd = CurrentCommand();
        if (_missingList.Count > 0 &&
            !Dialogs.Confirm(OwnerWindow, "Some values are still empty (" + string.Join(", ", _missingList.Distinct()) +
                                          "). GAM will probably reject the command. Run it anyway?", new[] { cmd }))
            return;

        string? confirm = cmd.LooksDestructive && _missingList.Count == 0 ? "This command can change data in your domain." : null;
        _cts = new CancellationTokenSource();
        try
        {
            await RunAsync(cmd, _results, confirm, ct: _cts.Token);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OpenTerminal()
    {
        try { Runner.OpenInTerminal(CurrentCommand()); }
        catch (Exception ex) { Dialogs.Error(OwnerWindow, ex.Message); }
    }
}
