using System.Data;
using System.Text;

namespace GamGui.Core;

public static class CsvTable
{
    /// <summary>RFC 4180 parser (quoted fields, doubled quotes, embedded newlines).</summary>
    public static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, fieldStarted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true; fieldStarted = true; break;
                case ',':
                    row.Add(field.ToString()); field.Clear(); fieldStarted = true; break;
                case '\r':
                    break;
                case '\n':
                    if (fieldStarted || field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
                    row = new List<string>(); field.Clear(); fieldStarted = false; break;
                default:
                    field.Append(c); fieldStarted = true; break;
            }
        }
        if (fieldStarted || field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>
    /// Tries to read GAM's CSV output. Returns null if the text doesn't look like CSV
    /// (for example the indented text that "info" commands print).
    /// </summary>
    public static DataTable? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var rows = ParseRows(text.TrimStart('\uFEFF'));
        if (rows.Count == 0) return null;
        var header = rows[0];
        if (header.Count == 0 || header.Any(h => h.Length == 0 || h.Length > 200)) return null;
        if (header.Count == 1 && rows.Count > 1 && rows.Skip(1).Any(r => r.Count != 1)) return null;
        // Most rows should match the header width.
        int matching = rows.Skip(1).Count(r => r.Count == header.Count);
        if (rows.Count > 1 && matching < (rows.Count - 1) * 0.9) return null;

        var table = new DataTable();
        for (int i = 0; i < header.Count; i++)
        {
            // Safe internal names; the real header goes in Caption (WPF bindings choke on dots in names).
            var col = table.Columns.Add("c" + i, typeof(string));
            col.Caption = header[i];
        }
        foreach (var r in rows.Skip(1))
        {
            var values = new object[header.Count];
            for (int i = 0; i < header.Count; i++) values[i] = i < r.Count ? r[i] : "";
            table.Rows.Add(values);
        }
        return table;
    }

    public static string ToCsv(DataView view)
    {
        var sb = new StringBuilder();
        var cols = view.Table!.Columns.Cast<DataColumn>().ToList();
        sb.AppendLine(string.Join(",", cols.Select(c => Escape(c.Caption))));
        foreach (DataRowView r in view)
            sb.AppendLine(string.Join(",", cols.Select(c => Escape(r[c.ColumnName]?.ToString() ?? ""))));
        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Finds a column by its original GAM header (case-insensitive).</summary>
    public static DataColumn? FindColumn(DataTable table, params string[] captions)
    {
        foreach (var cap in captions)
            foreach (DataColumn c in table.Columns)
                if (string.Equals(c.Caption, cap, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }
}
