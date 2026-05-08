using System;
using System.Collections.Generic;
using System.IO;

namespace HelpdeskHeroes;

/// <summary>
/// Manages files the user has staged for inclusion in the helpdesk
/// ticket. Ceiling math is set up against the Exchange Online inbound
/// limit (25 MB on the entire MIME message), backed off for base64 inflation
/// (~33%) and JSON / header overhead:
///
///   25 MB EXO inbound
///     ÷ 1.37  base64 + JSON escape inflation
///     − few MB  body, headers, other attachments
///   ≈ 18 MB  total raw attachment budget
///
/// Per-file ceiling is 15 MB so a single big file can't crowd out everything
/// else, but any reasonable photo/screenshot still fits comfortably.
///
/// Attachments are referenced by absolute path until send time — we don't
/// hold the bytes in memory while the user fills out the wizard. The
/// EmailSender reads + base64-encodes lazily during the JSON build, so
/// peak memory equals "one copy of the file + the encoded string" briefly,
/// not "every staged file × 2" the whole time.
///
/// Pasted clipboard images (from Win+Shift+S etc.) are written to %TEMP%
/// as PNG and added like any other file. We track which files came from
/// the clipboard so we can clean them up if the user removes them or
/// closes the wizard without sending.
/// </summary>
internal sealed class AttachmentSet
{
    /// <summary>Per-file size cap — see class docstring for the math.</summary>
    public const long MaxFileBytes = 15L * 1024 * 1024;

    /// <summary>Cumulative cap across all files.</summary>
    public const long MaxTotalBytes = 18L * 1024 * 1024;

    /// <summary>Max number of files. Belt-and-braces vs. a runaway add loop.</summary>
    public const int MaxFiles = 10;

    private readonly List<Attachment> _items = new();

    /// <summary>Read-only view for UI rendering.</summary>
    public IReadOnlyList<Attachment> Items => _items;

    public int Count => _items.Count;

    public long TotalBytes
    {
        get
        {
            long sum = 0;
            foreach (var a in _items) sum += a.SizeBytes;
            return sum;
        }
    }

    /// <summary>
    /// Try to add a file by path. Returns true on success; on rejection,
    /// <paramref name="error"/> contains a friendly reason suitable for
    /// MessageBox-ing at the user.
    /// </summary>
    public bool TryAdd(string path, out string error)
    {
        return TryAdd(path, isClipboardCapture: false, out error);
    }

    /// <summary>
    /// Internal add path that knows whether the file came from a clipboard
    /// capture (so we can later clean up the temp file).
    /// </summary>
    public bool TryAdd(string path, bool isClipboardCapture, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No file selected.";
            return false;
        }

        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch (Exception ex)
        {
            error = $"Couldn't read that file: {ex.Message}";
            return false;
        }

        if (!fi.Exists)
        {
            error = "That file doesn't exist anymore.";
            return false;
        }

        if (_items.Count >= MaxFiles)
        {
            error = $"Maximum {MaxFiles} attachments. Remove one before adding another.";
            return false;
        }

        if (fi.Length <= 0)
        {
            error = "That file is empty.";
            return false;
        }

        if (fi.Length > MaxFileBytes)
        {
            error = $"\"{fi.Name}\" is {FormatSize(fi.Length)} — bigger than the {FormatSize(MaxFileBytes)} per-file limit.";
            return false;
        }

        long projectedTotal = TotalBytes + fi.Length;
        if (projectedTotal > MaxTotalBytes)
        {
            error =
                $"Adding \"{fi.Name}\" ({FormatSize(fi.Length)}) would push the total over " +
                $"{FormatSize(MaxTotalBytes)}. Remove an attachment first.";
            return false;
        }

        // Dedupe on full path so accidental double-clicks don't re-add.
        foreach (var existing in _items)
        {
            if (string.Equals(existing.FullPath, fi.FullName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"\"{fi.Name}\" is already attached.";
                return false;
            }
        }

        _items.Add(new Attachment(fi.FullName, fi.Name, fi.Length, isClipboardCapture));
        return true;
    }

    /// <summary>
    /// Remove an attachment by path. If it was a clipboard-capture temp
    /// file, also delete the file from disk so we don't leak %TEMP% entries.
    /// </summary>
    public void Remove(string fullPath)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (!string.Equals(_items[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var a = _items[i];
            _items.RemoveAt(i);
            if (a.IsClipboardCapture)
            {
                try { File.Delete(a.FullPath); } catch { /* best effort */ }
            }
            return;
        }
    }

    /// <summary>
    /// Delete all clipboard-capture temp files. Called when the wizard
    /// closes so we don't leave PNGs laying around in %TEMP% indefinitely.
    /// </summary>
    public void CleanupClipboardTempFiles()
    {
        foreach (var a in _items)
        {
            if (!a.IsClipboardCapture) continue;
            try { File.Delete(a.FullPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Human-friendly byte formatter used in error messages and the UI.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }
}

/// <summary>One staged file. Bytes are not held — re-read at send time.</summary>
internal sealed record Attachment(
    string FullPath,
    string DisplayName,
    long SizeBytes,
    bool IsClipboardCapture);
