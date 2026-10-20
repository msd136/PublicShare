using System.Text;
using System.Text.RegularExpressions;

namespace GamGui.Core;

public sealed class GamCommandInfo
{
    private SyntaxNode? _node;

    public required string Section { get; init; }
    public required string Syntax { get; init; }
    public required string Title { get; init; }
    public string Notes { get; init; } = "";
    public int LineNumber { get; init; }

    public SyntaxNode Node => _node ??= BnfParser.Parse(Syntax);

    /// <summary>Lower-cased text used by the command search box.</summary>
    public string SearchText => (Section + " " + Syntax + " " + Notes).ToLowerInvariant();

    public override string ToString() => Title;
}

/// <summary>
/// Reads GAM's own GamCommands.txt so every GAM command (and every future one) gets a GUI form
/// without hand-written code.
/// </summary>
public sealed partial class CommandCatalog
{
    public List<string> Sections { get; } = new();
    public List<GamCommandInfo> Commands { get; } = new();
    public Dictionary<string, string> Definitions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Source { get; private set; } = "";

    private readonly Dictionary<string, IReadOnlyList<string>?> _choiceCache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^<([^<>]+)>\s*:{0,2}=\s*(.*)$")]
    private static partial Regex DefinitionRegex();

    // A comment is '#' after whitespace/'|' that is not a 6-digit hex colour like #ffffff.
    [GeneratedRegex(@"(^|[\s|])#(?![0-9A-Fa-f]{6}\b).*$")]
    private static partial Regex CommentRegex();

    public static CommandCatalog LoadFile(string path)
    {
        var cat = Parse(File.ReadAllText(path));
        cat.Source = path;
        return cat;
    }

    public static CommandCatalog LoadEmbedded()
    {
        using var stream = typeof(CommandCatalog).Assembly.GetManifestResourceStream("GamGui.Core.GamCommands.txt")
            ?? throw new InvalidOperationException("Embedded GamCommands.txt is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var cat = Parse(reader.ReadToEnd());
        cat.Source = "Built-in copy (GAM " + EmbeddedVersionHint + ")";
        return cat;
    }

    /// <summary>Version of GAM the embedded reference was taken from; informational only.</summary>
    public const string EmbeddedVersionHint = "7.x";

    /// <summary>Prefer the GamCommands.txt that ships next to gam.exe so the GUI matches the installed GAM.</summary>
    public static CommandCatalog LoadBest(string? explicitPath, string? gamExePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
                return LoadFile(explicitPath);
            if (!string.IsNullOrWhiteSpace(gamExePath))
            {
                var dir = Path.GetDirectoryName(gamExePath);
                if (dir != null)
                {
                    var candidate = Path.Combine(dir, "GamCommands.txt");
                    if (File.Exists(candidate)) return LoadFile(candidate);
                }
            }
        }
        catch
        {
            // Fall through to the embedded copy.
        }
        return LoadEmbedded();
    }

    public static CommandCatalog Parse(string text)
    {
        var cat = new CommandCatalog();
        string section = "General";
        string subsection = "";
        var notes = new List<string>();

        // Current block being accumulated.
        string? blockKind = null; // "cmd", "def", "note"
        var block = new List<string>();
        int blockLine = 0;

        void Finish()
        {
            if (blockKind == null) return;
            switch (blockKind)
            {
                case "cmd":
                    var syntax = string.Join("\n", block);
                    var noteText = string.Join("\n", notes).Trim();
                    if (subsection.Length > 0)
                        noteText = (subsection + (noteText.Length > 0 ? "\n" + noteText : "")).Trim();
                    cat.Commands.Add(new GamCommandInfo
                    {
                        Section = section,
                        Syntax = syntax,
                        Title = MakeTitle(block[0]),
                        Notes = noteText,
                        LineNumber = blockLine,
                    });
                    notes.Clear();
                    break;
                case "def":
                    var m = DefinitionRegex().Match(block[0]);
                    if (m.Success)
                    {
                        var body = new StringBuilder(StripComment(m.Groups[2].Value));
                        foreach (var l in block.Skip(1)) body.Append(' ').Append(StripComment(l).Trim());
                        cat.Definitions[m.Groups[1].Value.Trim()] = body.ToString().Trim();
                    }
                    break;
                case "note":
                    notes.AddRange(block.Select(b => b.Trim()));
                    break;
            }
            blockKind = null;
            block.Clear();
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].TrimEnd();
            if (line.Length == 0)
            {
                Finish();
                continue;
            }
            if (line.StartsWith("```")) continue;
            if (char.IsWhiteSpace(line[0]))
            {
                if (blockKind != null) block.Add(line);
                continue;
            }
            if (line.StartsWith('#'))
            {
                // Commented-out continuation lines such as "#       [batchsize <Integer>]".
                if (line.Length > 1 && char.IsWhiteSpace(line[1]) && line.TrimStart('#').TrimStart().StartsWith('['))
                    continue;
                Finish();
                if (line.StartsWith("## "))
                {
                    subsection = line[3..].Trim();
                }
                else if (line.StartsWith("# "))
                {
                    section = line[2..].Trim();
                    subsection = "";
                    notes.Clear();
                    if (!cat.Sections.Contains(section)) cat.Sections.Add(section);
                }
                continue;
            }

            Finish();
            blockLine = n + 1;
            if (line.StartsWith("gam ") || line == "gam")
            {
                blockKind = "cmd";
            }
            else if (DefinitionRegex().IsMatch(line))
            {
                blockKind = "def";
            }
            else
            {
                blockKind = "note";
            }
            block.Add(line);
        }
        Finish();

        // Sections that only contain definitions are noise in the command browser.
        var used = cat.Commands.Select(c => c.Section).ToHashSet();
        cat.Sections.RemoveAll(s => !used.Contains(s));
        if (used.Contains("General") && !cat.Sections.Contains("General")) cat.Sections.Insert(0, "General");
        return cat;
    }

    private static string StripComment(string s) => CommentRegex().Replace(s, "$1").TrimEnd();

    /// <summary>The first line up to the first optional/grouped part, e.g. "gam print users".</summary>
    private static string MakeTitle(string firstLine)
    {
        var idx = firstLine.IndexOfAny(new[] { '[', '(' });
        var t = (idx > 0 ? firstLine[..idx] : firstLine).Trim();
        return t.Length > 0 ? t : firstLine.Trim();
    }

    /// <summary>
    /// If a placeholder's definition is just a list of keywords (possibly through other such
    /// definitions), returns them so the GUI can offer a dropdown. Returns null otherwise.
    /// </summary>
    public IReadOnlyList<string>? GetLiteralChoices(string placeholder)
    {
        if (_choiceCache.TryGetValue(placeholder, out var cached)) return cached;
        _choiceCache[placeholder] = null; // guards against recursive definitions
        var result = ResolveChoices(placeholder, 0);
        _choiceCache[placeholder] = result;
        return result;
    }

    private IReadOnlyList<string>? ResolveChoices(string placeholder, int depth)
    {
        if (depth > 6 || !Definitions.TryGetValue(placeholder, out var def) || def.Length == 0) return null;
        SyntaxNode node;
        try { node = BnfParser.Parse(def); }
        catch { return null; }

        var options = node is ChoiceNode c ? c.Options : new[] { node };
        var list = new List<string>();
        foreach (var o in options)
        {
            switch (o)
            {
                case LiteralNode l:
                    var text = l.Text == "''" ? "" : l.Text;
                    if (text.Length > 0) list.Add(text);
                    break;
                case PlaceholderNode p:
                    var nested = depth < 6 ? ResolveChoices(p.Name, depth + 1) : null;
                    if (nested == null) return null;
                    list.AddRange(nested);
                    break;
                default:
                    return null;
            }
        }
        return list.Count > 0 && list.Count <= 1000 ? list.Distinct(StringComparer.Ordinal).ToList() : null;
    }

    public string? GetDefinition(string placeholder) =>
        Definitions.TryGetValue(placeholder, out var d) ? d : null;
}
