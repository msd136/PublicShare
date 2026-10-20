using System.Text;

namespace GamGui.Core;

/// <summary>A node in GAM's modified-BNF command syntax.</summary>
public abstract class SyntaxNode
{
    public abstract string ToSyntax();
    public override string ToString() => ToSyntax();
}

/// <summary>A fixed keyword such as <c>print</c> or <c>users</c>.</summary>
public sealed class LiteralNode : SyntaxNode
{
    public LiteralNode(string text) => Text = text;
    public string Text { get; }
    public override string ToSyntax() => Text;
}

/// <summary>A value the user supplies, such as <c>&lt;UserItem&gt;</c>.</summary>
public sealed class PlaceholderNode : SyntaxNode
{
    public PlaceholderNode(string name) => Name = name;
    public string Name { get; }
    public override string ToSyntax() => "<" + Name + ">";
}

public sealed class SequenceNode : SyntaxNode
{
    public SequenceNode(IReadOnlyList<SyntaxNode> items) => Items = items;
    public IReadOnlyList<SyntaxNode> Items { get; }
    public override string ToSyntax() => string.Join(" ", Items.Select(i => i.ToSyntax()));
}

/// <summary>Alternatives separated by <c>|</c>.</summary>
public sealed class ChoiceNode : SyntaxNode
{
    public ChoiceNode(IReadOnlyList<SyntaxNode> options) => Options = options;
    public IReadOnlyList<SyntaxNode> Options { get; }
    public bool IsAllLiterals => Options.All(o => o is LiteralNode);
    public override string ToSyntax() => "(" + string.Join("|", Options.Select(o => o.ToSyntax())) + ")";
}

/// <summary>An item in square brackets.</summary>
public sealed class OptionalNode : SyntaxNode
{
    public OptionalNode(SyntaxNode inner) => Inner = inner;
    public SyntaxNode Inner { get; }
    public override string ToSyntax()
    {
        var s = Inner.ToSyntax();
        if (Inner is ChoiceNode && s.StartsWith('(') && s.EndsWith(')')) s = s[1..^1];
        return "[" + s + "]";
    }
}

/// <summary>An item followed by <c>*</c> (zero or more) or <c>+</c> (one or more).</summary>
public sealed class RepeatNode : SyntaxNode
{
    public RepeatNode(SyntaxNode inner, bool oneOrMore) { Inner = inner; OneOrMore = oneOrMore; }
    public SyntaxNode Inner { get; }
    public bool OneOrMore { get; }
    public override string ToSyntax()
    {
        var s = Inner.ToSyntax();
        if (Inner is SequenceNode) s = "(" + s + ")";
        return s + (OneOrMore ? "+" : "*");
    }
}

/// <summary>
/// Parses GAM's modified BNF. The reference file is hand-written and has a few unbalanced
/// brackets, so the parser is deliberately forgiving: stray closers are skipped and unclosed
/// groups are closed at the end of input.
/// </summary>
public static class BnfParser
{
    private enum Kind { Literal, Placeholder, Open, Close, OpenOpt, CloseOpt, Bar, Star, Plus }

    private readonly record struct Token(Kind Kind, string Text);

    public static SyntaxNode Parse(string syntax)
    {
        var tokens = Tokenize(syntax);
        int pos = 0;
        return Simplify(ParseGroup(tokens, ref pos, topLevel: true));
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '[': list.Add(new(Kind.OpenOpt, "[")); i++; continue;
                case ']': list.Add(new(Kind.CloseOpt, "]")); i++; continue;
                case '(': list.Add(new(Kind.Open, "(")); i++; continue;
                case ')': list.Add(new(Kind.Close, ")")); i++; continue;
                case '|': list.Add(new(Kind.Bar, "|")); i++; continue;
            }
            if ((c == '*' || c == '+') && list.Count > 0 &&
                list[^1].Kind is Kind.Close or Kind.CloseOpt or Kind.Placeholder &&
                i > 0 && !char.IsWhiteSpace(s[i - 1]))
            {
                list.Add(new(c == '*' ? Kind.Star : Kind.Plus, c.ToString()));
                i++;
                continue;
            }
            if (c == '<')
            {
                int end = s.IndexOf('>', i + 1);
                int nextOpen = s.IndexOf('<', i + 1);
                bool valid = end > i + 1 && (nextOpen < 0 || nextOpen > end) && !s[(i + 1)..end].Contains('\n');
                if (valid)
                {
                    list.Add(new(Kind.Placeholder, s[(i + 1)..end].Trim()));
                    i = end + 1;
                    continue;
                }
            }
            // Literal: run until whitespace, a structural character, or the start of a placeholder.
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char d = s[i];
                if (char.IsWhiteSpace(d) || d is '[' or ']' or '(' or ')' or '|') break;
                if (d == '<' && sb.Length > 0) break;
                if (d == '<' && sb.Length == 0) { sb.Append(d); i++; continue; } // lone '<' operator
                sb.Append(d);
                i++;
            }
            if (sb.Length > 0) list.Add(new(Kind.Literal, sb.ToString()));
        }
        return list;
    }

    private static readonly object Bar = new();

    /// <summary>
    /// Parses items until the matching closer. GAM's notation differs from textbook BNF:
    /// a run of keywords joined by | are synonyms that bind tightly ("create|add user",
    /// "domain|domains &lt;X&gt;"). Other alternatives split the enclosing group, except at the
    /// top level of a command, where | always binds to its neighbours.
    /// </summary>
    private static SyntaxNode ParseGroup(List<Token> t, ref int pos, bool topLevel)
    {
        var elems = new List<object>();
        while (pos < t.Count)
        {
            var tok = t[pos];
            if (tok.Kind is Kind.Close or Kind.CloseOpt)
            {
                if (topLevel) { pos++; continue; } // stray closer in the reference file
                break;
            }
            if (tok.Kind == Kind.Bar) { elems.Add(Bar); pos++; continue; }

            SyntaxNode atom;
            switch (tok.Kind)
            {
                case Kind.Literal: atom = new LiteralNode(tok.Text); pos++; break;
                case Kind.Placeholder: atom = new PlaceholderNode(tok.Text); pos++; break;
                case Kind.OpenOpt:
                    pos++;
                    atom = new OptionalNode(ParseGroup(t, ref pos, false));
                    if (pos < t.Count && t[pos].Kind is Kind.CloseOpt or Kind.Close) pos++;
                    break;
                case Kind.Open:
                    pos++;
                    atom = ParseGroup(t, ref pos, false);
                    if (pos < t.Count && t[pos].Kind is Kind.Close or Kind.CloseOpt) pos++;
                    break;
                default: // stray * or +
                    atom = new LiteralNode(tok.Text); pos++; break;
            }
            while (pos < t.Count && t[pos].Kind is Kind.Star or Kind.Plus)
            {
                atom = new RepeatNode(atom, t[pos].Kind == Kind.Plus);
                pos++;
            }
            elems.Add(atom);
        }

        // Drop bars that don't sit between two items.
        for (int i = elems.Count - 1; i >= 0; i--)
        {
            if (elems[i] != Bar) continue;
            bool ok = i > 0 && i < elems.Count - 1 && elems[i - 1] != Bar && elems[i + 1] != Bar;
            if (!ok) elems.RemoveAt(i);
        }

        // Pass 1: keyword synonyms bind tightly.
        elems = MergeAdjacent(elems, (a, b) => IsLiteralish(a) && IsLiteralish(b));

        if (elems.Contains(Bar))
        {
            if (topLevel)
            {
                elems = MergeAdjacent(elems, (_, _) => true);
            }
            else
            {
                var options = new List<SyntaxNode>();
                var current = new List<SyntaxNode>();
                foreach (var e in elems)
                {
                    if (e == Bar) { options.Add(MakeSeq(current)); current = new(); }
                    else current.Add((SyntaxNode)e);
                }
                options.Add(MakeSeq(current));
                return new ChoiceNode(options.SelectMany(o => o is ChoiceNode c ? c.Options : new[] { o }).ToList());
            }
        }
        return MakeSeq(elems.Cast<SyntaxNode>().ToList());
    }

    private static bool IsLiteralish(object o) =>
        o is LiteralNode || (o is ChoiceNode c && c.IsAllLiterals);

    /// <summary>Joins "a | b" into a single choice wherever the predicate allows.</summary>
    private static List<object> MergeAdjacent(List<object> elems, Func<object, object, bool> canMerge)
    {
        var result = new List<object>();
        for (int i = 0; i < elems.Count; i++)
        {
            var e = elems[i];
            if (e == Bar && result.Count > 0 && i + 1 < elems.Count &&
                result[^1] != Bar && canMerge(result[^1], elems[i + 1]))
            {
                var left = (SyntaxNode)result[^1];
                var right = (SyntaxNode)elems[i + 1];
                var opts = new List<SyntaxNode>();
                opts.AddRange(left is ChoiceNode lc ? lc.Options : new[] { left });
                opts.AddRange(right is ChoiceNode rc ? rc.Options : new[] { right });
                result[^1] = new ChoiceNode(opts);
                i++;
                continue;
            }
            result.Add(e);
        }
        return result;
    }

    private static SyntaxNode MakeSeq(List<SyntaxNode> items)
    {
        var flat = Flatten(items);
        return flat.Count == 1 ? flat[0] : new SequenceNode(flat);
    }

    private static List<SyntaxNode> Flatten(IEnumerable<SyntaxNode> items)
    {
        var result = new List<SyntaxNode>();
        foreach (var i in items)
        {
            if (i is SequenceNode s) result.AddRange(Flatten(s.Items));
            else result.Add(i);
        }
        return result;
    }

    private static SyntaxNode Simplify(SyntaxNode n) => n switch
    {
        SequenceNode s when s.Items.Count == 1 => Simplify(s.Items[0]),
        _ => n
    };

    /// <summary>All literal keywords that appear in a tree (used for search and for detecting destructive commands).</summary>
    public static IEnumerable<string> Literals(SyntaxNode n)
    {
        switch (n)
        {
            case LiteralNode l: yield return l.Text; break;
            case SequenceNode s: foreach (var i in s.Items) foreach (var x in Literals(i)) yield return x; break;
            case ChoiceNode c: foreach (var i in c.Options) foreach (var x in Literals(i)) yield return x; break;
            case OptionalNode o: foreach (var x in Literals(o.Inner)) yield return x; break;
            case RepeatNode r: foreach (var x in Literals(r.Inner)) yield return x; break;
        }
    }
}
