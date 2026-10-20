using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GamGui.Core;

namespace GamGui.Views;

/// <summary>Ready-made jobs shown as cards: what each does, how, and a Start button.</summary>
public sealed class TasksView : ViewBase
{
    private const string AllCategories = "All categories";

    private readonly HintBox _search = new("Search tasks, e.g. offboard, inactive, mailbox", 320);
    private readonly ComboBox _category = Ui.Combo(new[] { AllCategories }.Concat(QuickTasks.Categories), 0, 200);
    private readonly WrapPanel _cards = new();
    private readonly TextBlock _resultsTitle = Ui.Text("Results", "H2");
    private readonly ResultView _results = new("Reports you run appear here. Tasks that change things show a summary when they finish.");

    public TasksView()
    {
        _search.Box.TextChanged += (_, _) => Refresh();
        _category.SelectionChanged += (_, _) => Refresh();

        var filters = Ui.Wrap(Ui.Margin(_search, 0, 0, 6, 6), Ui.Margin(_category, 0, 0, 6, 6));

        var scroller = new ScrollViewer
        {
            Content = _cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var splitter = new GridSplitter
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Rows,
            Cursor = System.Windows.Input.Cursors.SizeNS,
        };
        var results = Ui.Grid("auto,*").Add(Ui.Margin(_resultsTitle, 0, 4, 0, 6).At(0), _results.At(1));

        Content = Ui.Grid("auto,auto,3*,6,2*").Add(
            Header("Quick tasks", "Common admin jobs, ready to go. Each one explains what it does and which GAM command it runs."),
            filters.At(1),
            scroller.At(2),
            splitter.At(3),
            results.At(4));

        Refresh();
    }

    private void Refresh()
    {
        var terms = _search.Value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var category = _category.SelectedItem as string ?? AllCategories;
        _cards.Children.Clear();
        var matches = QuickTasks.All
            .Where(t => category == AllCategories || t.Category == category)
            .Where(t => terms.All(term => (t.Title + " " + t.What + " " + t.Category).ToLowerInvariant().Contains(term)))
            .ToList();
        foreach (var t in matches) _cards.Children.Add(Card(t));
        if (matches.Count == 0)
            _cards.Children.Add(Ui.Margin(Ui.Text("No tasks match. Try another word, or look in All commands, which has every GAM command.", "Muted"), 4, 8, 0, 0));
    }

    private Border Card(QuickTask task)
    {
        var how = new TextBlock
        {
            Text = task.How,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Ui.Brush("MutedBrush"),
        };
        var howBox = new Border
        {
            Background = Ui.Brush("CanvasBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 10),
            Child = Ui.Stack(Ui.Text("How it works", "Muted", 11.5), how),
        };

        var start = Ui.Button("Start", async (_, _) => await StartAsync(task), "PrimaryButton");
        var footer = Ui.Row(start);
        if (task.Changes)
        {
            footer.Children.Add(new Border
            {
                Background = Ui.Brush("DangerSoftBrush"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(4, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Makes changes", FontSize = 11.5, Foreground = Ui.Brush("DangerBrush") },
                ToolTip = "You'll see exactly what will change, and the GAM commands, before anything runs.",
            });
        }
        else
        {
            footer.Children.Add(new TextBlock
            {
                Text = "Read only",
                FontSize = 11.5,
                Foreground = Ui.Brush("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 6),
            });
        }

        return new Border
        {
            Style = Ui.Style("Card"),
            Width = 340,
            Child = Ui.Stack(
                Ui.Text(task.Category.ToUpperInvariant(), "Muted", 10.5),
                Ui.Margin(Ui.Text(task.Title, "H2"), 0, 2, 0, 4),
                Ui.Text(task.What),
                howBox,
                footer),
        };
    }

    private async Task StartAsync(QuickTask task)
    {
        var values = new Dictionary<string, string>();
        if (task.Fields.Length > 0)
        {
            var v = FormDialog.Ask(OwnerWindow, task.Title, task.What, task.Changes ? "Review" : "Run", task.Fields);
            if (v == null) return;
            values = v;
        }

        List<GamCommand> commands;
        try { commands = task.Build(values); }
        catch (Exception ex) { Dialogs.Error(OwnerWindow, ex.Message); return; }
        if (commands.Count == 0)
        {
            Dialogs.Info(OwnerWindow, "Nothing to do: no steps were selected.");
            return;
        }

        _resultsTitle.Text = "Results: " + task.Title;

        if (task.Changes)
        {
            await RunManyAsync(commands, task.What);
            return;
        }

        // Reports: one command, shown in the results grid.
        var r = await RunAsync(commands[0], _results);
        if (r != null && r.Success && task.Transform != null && _results.Table != null)
        {
            var filtered = task.Transform(_results.Table, values);
            _results.ShowTable(filtered);
        }
    }
}
