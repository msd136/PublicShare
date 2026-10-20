using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GamGui.Core;

namespace GamGui;

/// <summary>Small factory helpers so screens can be built in code with the app's styles.</summary>
public static class Ui
{
    public static object Res(string key) => Application.Current.FindResource(key);
    public static Brush Brush(string key) => (Brush)Res(key);
    public static Style Style(string key) => (Style)Res(key);

    public static Button Button(string text, RoutedEventHandler onClick, string? style = null, string? tip = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.Style = Style(style);
        if (tip != null) b.ToolTip = tip;
        b.Click += onClick;
        return b;
    }

    public static TextBlock Text(string text, string? style = null, double fontSize = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style != null) t.Style = Style(style);
        if (fontSize > 0) t.FontSize = fontSize;
        return t;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static WrapPanel Wrap(params UIElement[] items)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static T Margin<T>(T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static Border Panel(UIElement child)
    {
        return new Border { Style = Style("Panel"), Child = child };
    }

    /// <summary>Creates a grid with the given row heights ("auto", "*", "2*", or a number).</summary>
    public static Grid Grid(string rows, string columns = "*")
    {
        var g = new Grid();
        foreach (var r in rows.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            g.RowDefinitions.Add(new RowDefinition { Height = ParseLength(r) });
        foreach (var c in columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = ParseLength(c) });
        return g;
    }

    public static T At<T>(this T element, int row, int column = 0, int columnSpan = 1) where T : UIElement
    {
        System.Windows.Controls.Grid.SetRow(element, row);
        System.Windows.Controls.Grid.SetColumn(element, column);
        if (columnSpan > 1) System.Windows.Controls.Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    public static Grid Add(this Grid grid, params UIElement[] items)
    {
        foreach (var i in items) grid.Children.Add(i);
        return grid;
    }

    private static GridLength ParseLength(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s == "auto") return GridLength.Auto;
        if (s.EndsWith('*'))
        {
            var n = s.TrimEnd('*');
            return new GridLength(n.Length == 0 ? 1 : double.Parse(n, System.Globalization.CultureInfo.InvariantCulture), GridUnitType.Star);
        }
        return new GridLength(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, double width = double.NaN, bool editable = false)
    {
        var c = new ComboBox { IsEditable = editable, Width = width };
        foreach (var i in items) c.Items.Add(i);
        if (c.Items.Count > 0 && selected >= 0) c.SelectedIndex = Math.Min(selected, c.Items.Count - 1);
        return c;
    }

    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard busy; ignore */ }
    }
}

/// <summary>A text box with grey hint text shown while it is empty.</summary>
public sealed class HintBox : Grid
{
    private readonly TextBlock _hint;

    public HintBox(string hint, double width = double.NaN, bool mono = false)
    {
        Width = width;
        Box = new TextBox();
        if (mono) Box.FontFamily = (FontFamily)Ui.Res("MonoFont");
        _hint = new TextBlock
        {
            Text = hint,
            Foreground = Ui.Brush("MutedBrush"),
            Opacity = 0.8,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 9, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (mono) _hint.FontFamily = Box.FontFamily;
        Box.TextChanged += (_, _) => _hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(Box);
        Children.Add(_hint);
    }

    public TextBox Box { get; }

    public string Hint
    {
        get => _hint.Text;
        set => _hint.Text = value;
    }

    public string Text
    {
        get => Box.Text;
        set => Box.Text = value;
    }

    public string Value => Box.Text.Trim();

    /// <summary>Runs the action when Enter is pressed.</summary>
    public HintBox OnEnter(Action action)
    {
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; action(); }
        };
        return this;
    }
}

public enum FieldKind { Text, Password, Check, Choice, MultiLine }

public sealed record Field(
    string Key,
    string Label,
    string Default = "",
    FieldKind Kind = FieldKind.Text,
    IReadOnlyList<string>? Options = null,
    bool Required = false,
    string Hint = "");

/// <summary>A simple modal form built from a list of fields.</summary>
public sealed class FormDialog : Window
{
    private readonly Dictionary<string, Func<string>> _getters = new();
    private readonly List<(Field field, Func<string> get)> _required = new();
    private readonly TextBlock _error;

    private FormDialog(string title, string intro, IReadOnlyList<Field> fields, string okText)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)Ui.Res("UiFont");
        Background = Ui.Brush("SurfaceBrush");
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 12) };
        stack.Children.Add(Ui.Text(title, "H1"));
        if (intro.Length > 0) stack.Children.Add(Ui.Margin(Ui.Text(intro, "Muted"), 0, 2, 0, 12));

        FrameworkElement? first = null;
        foreach (var f in fields)
        {
            FrameworkElement input;
            Func<string> get;
            switch (f.Kind)
            {
                case FieldKind.Check:
                    var cb = new CheckBox { Content = f.Label, IsChecked = f.Default == "true", Margin = new Thickness(0, 6, 0, 6) };
                    input = cb;
                    get = () => cb.IsChecked == true ? "true" : "false";
                    stack.Children.Add(cb);
                    _getters[f.Key] = get;
                    continue;
                case FieldKind.Choice:
                    var combo = Ui.Combo(f.Options ?? Array.Empty<string>(), 0);
                    if (f.Default.Length > 0) combo.SelectedItem = f.Default;
                    input = combo;
                    get = () => combo.SelectedItem?.ToString() ?? "";
                    break;
                case FieldKind.MultiLine:
                    var mt = new TextBox
                    {
                        Text = f.Default, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                        Height = 110, VerticalContentAlignment = VerticalAlignment.Top,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    };
                    input = mt;
                    get = () => mt.Text.Trim();
                    break;
                default:
                    var tb = new TextBox { Text = f.Default };
                    if (f.Kind == FieldKind.Password) tb.FontFamily = (FontFamily)Ui.Res("MonoFont");
                    input = tb;
                    get = () => tb.Text.Trim();
                    break;
            }
            stack.Children.Add(Ui.Margin(Ui.Text(f.Label + (f.Required ? "" : " (optional)")), 0, 8, 0, 4));
            stack.Children.Add(input);
            if (f.Hint.Length > 0) stack.Children.Add(Ui.Margin(Ui.Text(f.Hint, "Muted", 12), 0, 3, 0, 0));
            _getters[f.Key] = get;
            if (f.Required) _required.Add((f, get));
            first ??= input;
        }

        _error = new TextBlock { Foreground = Ui.Brush("DangerBrush"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_error);

        var ok = Ui.Button(okText, (_, _) => Accept(), "PrimaryButton");
        ok.IsDefault = true;
        var cancel = Ui.Button("Cancel", (_, _) => { DialogResult = false; });
        cancel.IsCancel = true;
        var buttons = Ui.Row(ok, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(buttons);

        Content = stack;
        if (first != null) Loaded += (_, _) => first.Focus();
    }

    private void Accept()
    {
        var missing = _required.Where(r => r.get().Length == 0).Select(r => r.field.Label).ToList();
        if (missing.Count > 0)
        {
            _error.Text = "Fill in: " + string.Join(", ", missing);
            return;
        }
        DialogResult = true;
    }

    /// <summary>Returns the entered values keyed by field key, or null if cancelled.</summary>
    public static Dictionary<string, string>? Ask(Window? owner, string title, string intro, string okText, params Field[] fields)
    {
        var d = new FormDialog(title, intro, fields, okText);
        if (owner != null && owner.IsVisible) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (d.ShowDialog() != true) return null;
        return d._getters.ToDictionary(kv => kv.Key, kv => kv.Value());
    }

    /// <summary>Splits a list of emails/IDs typed with commas, spaces, or new lines.</summary>
    public static List<string> SplitList(string text) =>
        text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
