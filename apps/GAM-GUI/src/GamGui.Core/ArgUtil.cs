using System.Text;

namespace GamGui.Core;

public static class ArgUtil
{
    /// <summary>
    /// Splits a command line the way a user would type it at a Windows prompt:
    /// whitespace separates arguments, double quotes group them, and \" is a literal quote.
    /// Single quotes are kept as-is because GAM queries use them (e.g. name:'Jane Doe').
    /// </summary>
    public static List<string> Split(string? line)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return args;
        var sb = new StringBuilder();
        bool inQuotes = false, hasToken = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"'); i++; hasToken = true; continue;
            }
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken) { args.Add(sb.ToString()); sb.Clear(); hasToken = false; }
                continue;
            }
            sb.Append(c);
            hasToken = true;
        }
        if (hasToken) args.Add(sb.ToString());
        return args;
    }

    /// <summary>Quotes one argument for display (and for pasting into a command prompt).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        bool needs = arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || ch == '&' || ch == '|' || ch == '<' || ch == '>' || ch == '^');
        if (!needs) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    /// <summary>Removes a leading "gam" or "gam.exe" so pasted command lines work.</summary>
    public static List<string> StripGam(List<string> args)
    {
        if (args.Count > 0)
        {
            var first = Path.GetFileName(args[0]).ToLowerInvariant();
            if (first is "gam" or "gam.exe") args.RemoveAt(0);
        }
        return args;
    }
}
