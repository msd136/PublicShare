using System.IO;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GamGui.Core;
using Microsoft.Win32;

namespace GamGui;

/// <summary>
/// Shows GAM output: CSV from "print" commands becomes a sortable, filterable grid;
/// anything else is shown as text exactly as GAM wrote it.
/// </summary>
public sealed class ResultView : UserControl
{
    private readonly DataGrid _grid;
    private readonly TextBox _text;
    private readonly HintBox _filter;
    private readonly TextBlock _status;
    private readonly TextBlock _empty;
    private readonly Button _rawToggle;
    private readonly AdvicePanel _advice = new();
    private DataTable? _table;
    private GamResult? _last;
    private bool _showingRaw;
    private readonly string _emptyMessage;

    public event EventHandler? SelectionChanged;
    public event EventHandler? RowDoubleClick;

    public ResultView(string emptyMessage = "Results appear here.")
    {
        _emptyMessage = emptyMessage;
        _filter = new HintBox("Filter rows", 220).OnEnter(ApplyFilter);
        _filter.Box.TextChanged += (_, _) => ApplyFilter();
        _status = new TextBlock { Style = Ui.Style("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 6) };
        _rawToggle = Ui.Button("Show raw output", (_, _) => ToggleRaw());
        var copy = Ui.Button("Copy", (_, _) => CopyAll());
        var export = Ui.Button("Export CSV…", (_, _) => Export());

        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var right = Ui.Row(_rawToggle, copy, export);
        DockPanel.SetDock(right, Dock.Right);
        toolbar.Children.Add(right);
        toolbar.Children.Add(Ui.Row(Ui.Margin(_filter, 0, 0, 0, 6), _status));

        _grid = new DataGrid { AutoGenerateColumns = true };
        _grid.AutoGeneratingColumn += OnAutoGeneratingColumn;
        _grid.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement fe && fe.DataContext is DataRowView)
                RowDoubleClick?.Invoke(this, EventArgs.Empty);
        };
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopySelection(); e.Handled = true; }
        };

        _text = new TextBox { Style = Ui.Style("MonoText") };
        _empty = new TextBlock
        {
            Text = emptyMessage,
            Style = Ui.Style("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(20),
        };

        var body = new Grid();
        body.Children.Add(_grid);
        body.Children.Add(_text);
        body.Children.Add(_empty);

        Content = Ui.Grid("auto,auto,*").Add(toolbar.At(0), Ui.Margin(_advice, 0, 0, 0, 8).At(1), body.At(2));
        ShowEmpty();
    }

    public DataTable? Table => _table;

    public IReadOnlyList<DataRowView> SelectedRows =>
        _grid.SelectedItems.OfType<DataRowView>().ToList();

    /// <summary>Values of one column for the selected rows, looked up by GAM's header name.</summary>
    public List<string> SelectedValues(params string[] captions)
    {
        if (_table == null) return new();
        var col = CsvTable.FindColumn(_table, captions);
        if (col == null) return new();
        return SelectedRows.Select(r => r[col.ColumnName]?.ToString() ?? "")
            .Where(v => v.Length > 0).ToList();
    }

    public string? FirstSelected(params string[] captions) => SelectedValues(captions).FirstOrDefault();

    public void ShowEmpty()
    {
        _table = null;
        _grid.ItemsSource = null;
        _grid.Visibility = Visibility.Collapsed;
        _text.Visibility = Visibility.Collapsed;
        _empty.Visibility = Visibility.Visible;
        _status.Text = "";
        _empty.Text = _emptyMessage;
        _advice.Hide();
        _rawToggle.IsEnabled = false;
    }

    public void ShowRunning(string display)
    {
        ShowEmpty();
        _empty.Text = "Running…\n" + display;
        _status.Text = "Running…";
    }

    public void ShowText(string text, bool isError = false)
    {
        _table = null;
        _grid.ItemsSource = null;
        _grid.Visibility = Visibility.Collapsed;
        _empty.Visibility = Visibility.Collapsed;
        _text.Visibility = Visibility.Visible;
        _text.Text = text;
        _text.Foreground = isError ? Ui.Brush("DangerBrush") : Ui.Brush("InkBrush");
        _status.Text = isError ? "Failed" : "";
        _showingRaw = false;
        _rawToggle.Content = "Show raw output";
    }

    /// <summary>Displays a finished GAM run.</summary>
    public void ShowResult(GamResult result, bool expectCsv)
    {
        _last = result;
        _advice.Show(ErrorAdvisor.Diagnose(result));
        _rawToggle.IsEnabled = true;
        _showingRaw = false;
        _rawToggle.Content = "Show raw output";

        if (result.Cancelled)
        {
            ShowText("Stopped.\n\n" + result.StdErr + result.StdOut);
            _status.Text = "Stopped";
            return;
        }

        var table = expectCsv ? CsvTable.TryParse(result.StdOut) : null;
        if (table != null)
        {
            ShowTable(table);
            var errors = result.StdErr.Split('\n').Count(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
            _status.Text = $"{table.Rows.Count:N0} rows · {result.Elapsed.TotalSeconds:0.0} s" +
                           (errors > 0 ? $" · {errors} error line(s), see raw output" : "") +
                           (result.ExitCode != 0 ? $" · exit code {result.ExitCode}" : "");
            return;
        }

        var text = result.StdOut;
        if (result.StdErr.Length > 0)
            text = text.Length > 0 ? text.TrimEnd() + "\n\n" + result.StdErr : result.StdErr;
        if (text.Trim().Length == 0) text = result.Success ? "Done. GAM returned no output." : "GAM returned no output.";
        ShowText(text, !result.Success);
        _status.Text = result.Success
            ? $"Done · {result.Elapsed.TotalSeconds:0.0} s"
            : $"GAM exited with code {result.ExitCode}";
        _rawToggle.IsEnabled = false;
    }

    public void ShowTable(DataTable table)
    {
        _table = table;
        _grid.ItemsSource = null;
        _grid.Columns.Clear();
        _grid.ItemsSource = table.DefaultView;
        _grid.Visibility = Visibility.Visible;
        _text.Visibility = Visibility.Collapsed;
        _empty.Visibility = table.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = table.Rows.Count == 0 ? "No matching items." : "";
        ApplyFilter();
    }

    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var caption = _table?.Columns.Contains(e.PropertyName) == true ? _table.Columns[e.PropertyName]!.Caption : e.PropertyName;
        // A TextBlock header keeps underscores visible (strings are treated as access keys).
        e.Column.Header = new TextBlock { Text = caption };
        e.Column.MaxWidth = 420;
        e.Column.SortMemberPath = e.PropertyName;
    }

    private void ApplyFilter()
    {
        if (_table == null) return;
        var f = _filter.Value;
        if (f.Length == 0)
        {
            _table.DefaultView.RowFilter = "";
            return;
        }
        var escaped = EscapeLike(f);
        _table.DefaultView.RowFilter = string.Join(" OR ",
            _table.Columns.Cast<DataColumn>().Select(c => $"[{c.ColumnName}] LIKE '%{escaped}%'"));
    }

    private static string EscapeLike(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\'': sb.Append("''"); break;
                case '[': case ']': case '*': case '%': sb.Append('[').Append(ch).Append(']'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private void ToggleRaw()
    {
        if (_last == null) return;
        if (!_showingRaw)
        {
            var keep = _table;
            ShowText((_last.StdOut + (_last.StdErr.Length > 0 ? "\n--- stderr ---\n" + _last.StdErr : "")).TrimEnd());
            _table = keep;
            _showingRaw = true;
            _rawToggle.Content = "Show table";
            _rawToggle.IsEnabled = true;
        }
        else if (_table != null)
        {
            _showingRaw = false;
            _rawToggle.Content = "Show raw output";
            ShowTable(_table);
        }
    }

    private void CopyAll()
    {
        if (_table != null && _grid.Visibility == Visibility.Visible) Ui.Copy(CsvTable.ToCsv(_table.DefaultView));
        else if (_text.Visibility == Visibility.Visible) Ui.Copy(_text.Text);
    }

    private void CopySelection()
    {
        if (_table == null) return;
        var cols = _table.Columns.Cast<DataColumn>().ToList();
        var lines = SelectedRows.Select(r => string.Join("\t", cols.Select(c => r[c.ColumnName]?.ToString() ?? "")));
        Ui.Copy(string.Join("\r\n", lines));
    }

    private void Export()
    {
        string content;
        string ext;
        if (_table != null && _grid.Visibility == Visibility.Visible) { content = CsvTable.ToCsv(_table.DefaultView); ext = "csv"; }
        else if (_text.Visibility == Visibility.Visible) { content = _text.Text; ext = "txt"; }
        else return;

        var dlg = new SaveFileDialog
        {
            Filter = ext == "csv" ? "CSV files (*.csv)|*.csv|All files (*.*)|*.*" : "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "gam-output." + ext,
        };
        var owner = Window.GetWindow(this);
        var ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
        if (ok == true)
            File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(true));
    }
}
