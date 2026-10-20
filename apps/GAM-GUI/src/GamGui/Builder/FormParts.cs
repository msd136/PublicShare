using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GamGui.Core;

namespace GamGui.Builder;

/// <summary>
/// One piece of a generated command form. Each part knows how to draw itself and how to
/// emit its arguments. Parts inside unchecked optional sections emit nothing.
/// </summary>
public abstract class FormPart
{
    public event EventHandler? Changed;

    public abstract FrameworkElement View { get; }

    /// <summary>Appends this part's arguments; records unfilled required values in <paramref name="missing"/>.</summary>
    public abstract void Emit(List<string> args, List<string> missing);

    /// <summary>Inline parts flow on one line; block parts get their own row.</summary>
    public virtual bool IsInline => false;

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    protected T Hook<T>(T child) where T : FormPart
    {
        child.Changed += (_, _) => RaiseChanged();
        return child;
    }
}

public static class FormBuilder
{
    private const int MaxDefinitionDepth = 3;

    public static FormPart Build(SyntaxNode node, CommandCatalog catalog, int depth = 0) => node switch
    {
        LiteralNode l => new LiteralPart(l.Text),
        PlaceholderNode p => new PlaceholderPart(p.Name, catalog, depth),
        ChoiceNode c when c.IsAllLiterals => new LiteralChoicePart(c.Options.Cast<LiteralNode>().Select(o => o.Text).ToList()),
        ChoiceNode c => new ChoicePart(c, catalog, depth),
        OptionalNode o => new OptionalPart(o, catalog, depth),
        RepeatNode r => new RepeatPart(r, catalog, depth),
        SequenceNode s => new SequencePart(s.Items.Select(i => Build(i, catalog, depth)).ToList()),
        _ => new LiteralPart(node.ToSyntax()),
    };

    internal static string Short(string s, int max = 90)
    {
        s = s.Replace('\n', ' ');
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    internal static TextBlock Keyword(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Ui.Res("MonoFont"),
        FontWeight = FontWeights.SemiBold,
        Foreground = Ui.Brush("AccentBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 6),
    };

    internal static bool IsDepthOk(int depth) => depth < MaxDefinitionDepth;
}

/// <summary>A fixed keyword.</summary>
public sealed class LiteralPart : FormPart
{
    private readonly string _text;
    private readonly TextBlock _view;

    public LiteralPart(string text)
    {
        _text = text;
        _view = FormBuilder.Keyword(text);
    }

    public override FrameworkElement View => _view;
    public override bool IsInline => true;
    public override void Emit(List<string> args, List<string> missing) => args.Add(_text);
}

/// <summary>Keyword synonyms such as create|add. The first is chosen by default.</summary>
public sealed class LiteralChoicePart : FormPart
{
    private readonly ComboBox _combo;

    public LiteralChoicePart(IReadOnlyList<string> options)
    {
        _combo = Ui.Combo(options, 0);
        _combo.FontFamily = (FontFamily)Ui.Res("MonoFont");
        _combo.MinWidth = 90;
        _combo.Margin = new Thickness(0, 0, 8, 6);
        _combo.SelectionChanged += (_, _) => RaiseChanged();
    }

    public override FrameworkElement View => _combo;
    public override bool IsInline => true;

    public override void Emit(List<string> args, List<string> missing)
    {
        if (_combo.SelectedItem is string s) args.Add(s);
    }
}

/// <summary>
/// A value such as &lt;UserItem&gt;. Keyword-only definitions become dropdowns; structured
/// definitions (like &lt;UserTypeEntity&gt;) can switch to a guided sub-form.
/// </summary>
public sealed class PlaceholderPart : FormPart
{
    private readonly string _name;
    private readonly CommandCatalog _catalog;
    private readonly int _depth;
    private readonly TextBox? _text;
    private readonly ComboBox? _combo;
    private readonly ToggleButton? _guidedToggle;
    private readonly ContentControl _guidedHost = new();
    private FormPart? _guided;
    private Border? _guidedBorder;
    private readonly StackPanel _view;

    public PlaceholderPart(string name, CommandCatalog catalog, int depth)
    {
        _name = name;
        _catalog = catalog;
        _depth = depth;
        var definition = catalog.GetDefinition(name);
        var choices = catalog.GetLiteralChoices(name);

        var label = new TextBlock
        {
            Text = "<" + name + ">",
            Foreground = Ui.Brush("MutedBrush"),
            FontSize = 11.5,
            Margin = new Thickness(1, 0, 0, 2),
        };
        FrameworkElement input;
        if (choices != null)
        {
            _combo = new ComboBox { IsEditable = true, MinWidth = 160, MaxWidth = 360 };
            foreach (var c in choices) _combo.Items.Add(c);
            _combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, _) => RaiseChanged()));
            _combo.SelectionChanged += (_, _) => RaiseChanged();
            input = _combo;
        }
        else
        {
            _text = new TextBox { MinWidth = 200, MaxWidth = 520 };
            _text.TextChanged += (_, _) => RaiseChanged();
            input = _text;
        }

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal };
        inputRow.Children.Add(input);

        // Structured definitions (entities, attribute lists…) can be filled in with a guided form.
        if (choices == null && definition != null && FormBuilder.IsDepthOk(depth) && HasStructure(definition))
        {
            _guidedToggle = new ToggleButton
            {
                Content = "Guided",
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
                ToolTip = "Fill this value in with a form built from its definition",
            };
            _guidedToggle.Checked += (_, _) => SetGuided(true);
            _guidedToggle.Unchecked += (_, _) => SetGuided(false);
            inputRow.Children.Add(_guidedToggle);
        }

        _view = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
        _view.Children.Add(label);
        _view.Children.Add(inputRow);
        _view.Children.Add(_guidedHost);

        if (definition != null)
        {
            _view.ToolTip = new TextBlock
            {
                Text = "<" + name + "> ::= " + FormBuilder.Short(definition, 1200),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                FontFamily = (FontFamily)Ui.Res("MonoFont"),
            };
            ToolTipService.SetShowDuration(_view, 60000);
        }
    }

    private static bool HasStructure(string definition)
    {
        if (definition.Length < 3) return false;
        try
        {
            var n = BnfParser.Parse(definition);
            return n is not PlaceholderNode && BnfParser.Literals(n).Any(l => l.Length > 1 && char.IsLetter(l[0]));
        }
        catch { return false; }
    }

    private void SetGuided(bool on)
    {
        if (on && _guided == null)
        {
            var def = _catalog.GetDefinition(_name)!;
            _guided = Hook(FormBuilder.Build(BnfParser.Parse(def), _catalog, _depth + 1));
            _guidedBorder = new Border
            {
                BorderBrush = Ui.Brush("LineBrush"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(10, 6, 0, 0),
                Margin = new Thickness(2, 6, 0, 0),
                Child = _guided.View,
            };
        }
        _guidedHost.Content = on ? _guidedBorder : null;
        if (_text != null) _text.IsEnabled = !on;
        RaiseChanged();
    }

    public override FrameworkElement View => _view;
    public override bool IsInline => true;

    public override void Emit(List<string> args, List<string> missing)
    {
        if (_guidedToggle?.IsChecked == true && _guided != null)
        {
            _guided.Emit(args, missing);
            return;
        }
        var value = (_combo?.Text ?? _text?.Text ?? "").Trim();
        if (value.Length == 0)
        {
            missing.Add("<" + _name + ">");
            return;
        }
        if (SplitsIntoWords(_name) || value.Contains('"')) args.AddRange(ArgUtil.Split(value));
        else args.Add(value);
    }

    /// <summary>
    /// Entity and attribute placeholders stand for several words (e.g. "ou /Sales"),
    /// so their text is split; everything else is passed as one argument, spaces included.
    /// </summary>
    private static bool SplitsIntoWords(string name) =>
        name.Contains("Entity", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Attributes", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Selector", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Shortcut", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Content", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Match", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Select", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Redirect", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Config", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Alternatives with structure: a dropdown picks one, and its form appears below.</summary>
public sealed class ChoicePart : FormPart
{
    private readonly ComboBox _combo;
    private readonly ContentControl _host = new();
    private readonly List<FormPart?> _parts;
    private readonly List<Border?> _wrappers;
    private readonly IReadOnlyList<SyntaxNode> _options;
    private readonly CommandCatalog _catalog;
    private readonly int _depth;
    private readonly StackPanel _view;

    public ChoicePart(ChoiceNode node, CommandCatalog catalog, int depth)
    {
        _options = node.Options;
        _catalog = catalog;
        _depth = depth;
        _parts = _options.Select(_ => (FormPart?)null).ToList();
        _wrappers = _options.Select(_ => (Border?)null).ToList();
        _combo = Ui.Combo(_options.Select(o => FormBuilder.Short(o.ToSyntax())), 0);
        _combo.FontFamily = (FontFamily)Ui.Res("MonoFont");
        _combo.MaxWidth = 640;
        _combo.HorizontalAlignment = HorizontalAlignment.Left;
        _combo.Margin = new Thickness(0, 0, 0, 6);
        _combo.SelectionChanged += (_, _) => { ShowSelected(); RaiseChanged(); };

        _view = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        _view.Children.Add(new TextBlock { Text = "Choose one", Foreground = Ui.Brush("MutedBrush"), FontSize = 11.5, Margin = new Thickness(1, 0, 0, 2) });
        _view.Children.Add(_combo);
        _view.Children.Add(_host);
        ShowSelected();
    }

    private FormPart? Selected
    {
        get
        {
            var i = _combo.SelectedIndex;
            if (i < 0) return null;
            // Build alternatives lazily: some commands have dozens of large ones.
            return _parts[i] ??= Hook(FormBuilder.Build(_options[i], _catalog, _depth));
        }
    }

    private void ShowSelected()
    {
        var part = Selected;
        var i = _combo.SelectedIndex;
        if (part == null || i < 0) { _host.Content = null; return; }
        // Each view gets one wrapper for its lifetime; WPF elements can't have two parents.
        _host.Content = _wrappers[i] ??= new Border
        {
            BorderBrush = Ui.Brush("LineBrush"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 0),
            Margin = new Thickness(2, 0, 0, 0),
            Child = part.View,
        };
    }

    public override FrameworkElement View => _view;

    public override void Emit(List<string> args, List<string> missing) => Selected?.Emit(args, missing);
}

/// <summary>[optional] — a check box that reveals its contents.</summary>
public sealed class OptionalPart : FormPart
{
    private readonly CheckBox _check;
    private readonly LiteralChoicePart? _flagChoice;
    private Border? _wrapper;
    private readonly string? _flag;
    private readonly ContentControl _host = new();
    private readonly StackPanel _view;
    private readonly SyntaxNode _node;
    private readonly CommandCatalog _catalog;
    private readonly int _depth;
    private FormPart? _lazyInner;

    public OptionalPart(OptionalNode node, CommandCatalog catalog, int depth)
    {
        _node = node.Inner;
        _catalog = catalog;
        _depth = depth;
        _view = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        if (node.Inner is LiteralNode lit)
        {
            // [doit] — just a check box.
            _flag = lit.Text;
            _check = new CheckBox { Content = Mono(lit.Text), Margin = new Thickness(0, 2, 0, 4) };
            _view.Children.Add(_check);
        }
        else if (node.Inner is ChoiceNode c && c.IsAllLiterals)
        {
            // [ascending|descending] — check box plus a dropdown.
            _flagChoice = Hook(new LiteralChoicePart(c.Options.Cast<LiteralNode>().Select(o => o.Text).ToList()));
            _check = new CheckBox { Content = Mono(FormBuilder.Short(node.ToSyntax())), Margin = new Thickness(0, 2, 0, 4) };
            _view.Children.Add(_check);
            _view.Children.Add(_host);
        }
        else
        {
            _check = new CheckBox { Content = Mono(FormBuilder.Short(node.ToSyntax(), 110)), Margin = new Thickness(0, 2, 0, 4) };
            _view.Children.Add(_check);
            _view.Children.Add(_host);
        }

        _check.Checked += (_, _) => { Expand(true); RaiseChanged(); };
        _check.Unchecked += (_, _) => { Expand(false); RaiseChanged(); };
    }

    private static TextBlock Mono(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Ui.Res("MonoFont"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
    };

    private FormPart? Inner
    {
        get
        {
            if (_flag != null) return null;
            if (_flagChoice != null) return _flagChoice;
            return _lazyInner ??= Hook(FormBuilder.Build(_node, _catalog, _depth));
        }
    }

    private void Expand(bool on)
    {
        if (_flag != null) return;
        var inner = Inner;
        if (on && inner != null)
        {
            _wrapper ??= new Border
            {
                BorderBrush = Ui.Brush("AccentSoftBrush"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(12, 4, 0, 2),
                Margin = new Thickness(7, 0, 0, 6),
                Child = inner.View,
            };
            _host.Content = _wrapper;
        }
        else _host.Content = null;
    }

    public override FrameworkElement View => _view;

    public override void Emit(List<string> args, List<string> missing)
    {
        if (_check.IsChecked != true) return;
        if (_flag != null) { args.Add(_flag); return; }
        Inner?.Emit(args, missing);
    }
}

/// <summary>item* or item+ — a list of entries with add/remove buttons.</summary>
public sealed class RepeatPart : FormPart
{
    private readonly RepeatNode _node;
    private readonly CommandCatalog _catalog;
    private readonly int _depth;
    private readonly List<FormPart> _items = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _view;

    public RepeatPart(RepeatNode node, CommandCatalog catalog, int depth)
    {
        _node = node;
        _catalog = catalog;
        _depth = depth;
        var header = new TextBlock
        {
            Text = FormBuilder.Short(node.ToSyntax(), 110) + (node.OneOrMore ? "   (one or more)" : "   (any number)"),
            FontFamily = (FontFamily)Ui.Res("MonoFont"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
        };
        var add = new Button { Content = "Add", Margin = new Thickness(10, 0, 0, 0), MinHeight = 24, Padding = new Thickness(10, 1, 10, 1) };
        add.Click += (_, _) => AddItem();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        top.Children.Add(header);
        top.Children.Add(add);

        _view = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        _view.Children.Add(top);
        _view.Children.Add(_list);
        if (node.OneOrMore) AddItem();
    }

    private void AddItem()
    {
        var part = Hook(FormBuilder.Build(_node.Inner, _catalog, _depth));
        _items.Add(part);
        var remove = new Button { Content = "Remove", Margin = new Thickness(8, 14, 0, 0), MinHeight = 24, Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Top };
        var row = new DockPanel { Margin = new Thickness(7, 0, 0, 2) };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(new Border
        {
            BorderBrush = Ui.Brush("LineBrush"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 0),
            Child = part.View,
        });
        remove.Click += (_, _) =>
        {
            _items.Remove(part);
            _list.Children.Remove(row);
            RaiseChanged();
        };
        _list.Children.Add(row);
        RaiseChanged();
    }

    public override FrameworkElement View => _view;

    public override void Emit(List<string> args, List<string> missing)
    {
        if (_items.Count == 0 && _node.OneOrMore) missing.Add(FormBuilder.Short(_node.Inner.ToSyntax(), 40));
        foreach (var i in _items) i.Emit(args, missing);
    }
}

/// <summary>A run of parts: inline parts flow together; block parts get their own row.</summary>
public sealed class SequencePart : FormPart
{
    private readonly List<FormPart> _parts;
    private readonly StackPanel _view = new();

    public SequencePart(List<FormPart> parts)
    {
        _parts = parts.Select(p => Hook(p)).ToList();
        WrapPanel? line = null;
        foreach (var p in _parts)
        {
            if (p.IsInline)
            {
                if (line == null)
                {
                    line = new WrapPanel { Orientation = Orientation.Horizontal };
                    _view.Children.Add(line);
                }
                line.Children.Add(p.View);
            }
            else
            {
                line = null;
                _view.Children.Add(p.View);
            }
        }
    }

    public override FrameworkElement View => _view;

    public override void Emit(List<string> args, List<string> missing)
    {
        foreach (var p in _parts) p.Emit(args, missing);
    }
}
