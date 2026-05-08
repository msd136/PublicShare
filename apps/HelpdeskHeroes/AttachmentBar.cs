using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HelpdeskHeroes;

/// <summary>
/// Compact horizontal bar that lives in the wizard footer and shows staged
/// attachments plus the "📎 Attach" / "📋 Paste" buttons. Persists across
/// page navigation because it lives on <see cref="GetHelpForm"/> directly,
/// not on individual <see cref="WizardPage"/> instances — so a user can
/// add a screenshot on the password page, then navigate away and back, and
/// the screenshot is still there.
///
/// The bar is bound to the report's <see cref="AttachmentSet"/>, not its
/// own list — so quick-flow callers (MainForm.QuickIssueDialog) can also
/// drop one in and share the same data.
/// </summary>
internal sealed class AttachmentBar : UserControl
{
    private static readonly Color BrandBlue = Color.FromArgb(0, 90, 158);
    private static readonly Color SoftGrey  = Color.FromArgb(245, 247, 250);
    private static readonly Color TextDim   = Color.FromArgb(90, 90, 90);
    private static readonly Color WarnRed   = Color.FromArgb(180, 60, 30);

    private readonly AttachmentSet _set;
    private readonly Label _summary;
    private readonly FlowLayoutPanel _chipRow;
    private readonly Button _attachBtn;
    private readonly Button _pasteBtn;

    public AttachmentBar(AttachmentSet set)
    {
        _set = set ?? throw new ArgumentNullException(nameof(set));

        BackColor = SoftGrey;
        Height    = 64;
        Dock      = DockStyle.Bottom;
        Padding   = new Padding(16, 8, 16, 8);

        // ---- Buttons (docked left) ----
        _attachBtn = new Button
        {
            Text      = "📎  Attach",
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            BackColor = Color.White,
            ForeColor = BrandBlue,
            Size      = new Size(96, 32),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Location  = new Point(16, 16)
        };
        _attachBtn.FlatAppearance.BorderColor = BrandBlue;
        _attachBtn.FlatAppearance.BorderSize  = 1;
        _attachBtn.Click += (_, _) => OnAttachClicked();
        new ToolTip().SetToolTip(_attachBtn, "Pick a file to attach to your ticket");

        _pasteBtn = new Button
        {
            Text      = "📋  Paste",
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            BackColor = Color.White,
            ForeColor = BrandBlue,
            Size      = new Size(96, 32),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Location  = new Point(120, 16)
        };
        _pasteBtn.FlatAppearance.BorderColor = BrandBlue;
        _pasteBtn.FlatAppearance.BorderSize  = 1;
        _pasteBtn.Click += (_, _) => OnPasteClicked();
        new ToolTip().SetToolTip(_pasteBtn,
            "Paste an image from the clipboard (e.g. a snip taken with Win + Shift + S)");

        Controls.Add(_attachBtn);
        Controls.Add(_pasteBtn);

        // ---- Chip row (the staged files) ----
        // Sits to the right of the buttons. Uses a horizontally-scrolling
        // FlowLayoutPanel so 5+ attachments don't break the layout.
        _chipRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoScroll    = true,
            Location      = new Point(228, 8),
            Size          = new Size(380, 48),
            BackColor     = Color.Transparent,
            Padding       = new Padding(0, 8, 0, 0)
        };
        Controls.Add(_chipRow);

        // ---- Summary label (top-right) ----
        _summary = new Label
        {
            AutoSize  = true,
            Font      = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = TextDim,
            Location  = new Point(228, 0)
        };
        Controls.Add(_summary);

        // Reposition summary + chip row when the bar resizes.
        Resize += (_, _) => RepositionLayout();

        RefreshChips();
    }

    private void RepositionLayout()
    {
        // Buttons stay anchored at left. Chip row + summary fill the rest.
        int leftEdge = _pasteBtn.Right + 16;
        int rightEdge = ClientSize.Width - 16;
        int width = Math.Max(0, rightEdge - leftEdge);
        _summary.Location = new Point(leftEdge, 4);
        _chipRow.Location = new Point(leftEdge, 22);
        _chipRow.Size     = new Size(width, 38);
    }

    /// <summary>Re-render the chip row to match the underlying AttachmentSet.</summary>
    public void RefreshChips()
    {
        _chipRow.SuspendLayout();
        _chipRow.Controls.Clear();

        foreach (var a in _set.Items)
        {
            _chipRow.Controls.Add(BuildChip(a));
        }

        _chipRow.ResumeLayout();
        UpdateSummary();
        RepositionLayout();
    }

    private void UpdateSummary()
    {
        if (_set.Count == 0)
        {
            _summary.Text      = "No attachments. Up to " +
                                 $"{AttachmentSet.FormatSize(AttachmentSet.MaxTotalBytes)} total, " +
                                 $"{AttachmentSet.FormatSize(AttachmentSet.MaxFileBytes)} per file.";
            _summary.ForeColor = TextDim;
        }
        else
        {
            long total = _set.TotalBytes;
            string label =
                $"{_set.Count} attachment{(_set.Count == 1 ? "" : "s")} • " +
                $"{AttachmentSet.FormatSize(total)} of " +
                $"{AttachmentSet.FormatSize(AttachmentSet.MaxTotalBytes)}";
            _summary.Text      = label;
            // Turn the summary red as we approach the cap so the user
            // notices before a send tries to fail.
            _summary.ForeColor = total > AttachmentSet.MaxTotalBytes * 0.85
                                  ? WarnRed
                                  : TextDim;
        }
    }

    /// <summary>
    /// Build one "chip" — a small rounded panel with the filename, size,
    /// and an X to remove. Using a Panel + manual layout because the bare
    /// FlowLayoutPanel doesn't give us a remove affordance.
    /// </summary>
    private Control BuildChip(Attachment a)
    {
        var chip = new Panel
        {
            BackColor = Color.White,
            Margin    = new Padding(0, 0, 6, 0),
            Padding   = new Padding(8, 4, 4, 4),
            Height    = 28
        };

        var label = new Label
        {
            Text      = $"{Truncate(a.DisplayName, 28)} · {AttachmentSet.FormatSize(a.SizeBytes)}",
            Font      = new Font("Segoe UI", 8.5F),
            ForeColor = Color.Black,
            AutoSize  = true,
            Location  = new Point(8, 6)
        };
        chip.Controls.Add(label);

        // Compute chip width based on label after it sizes itself.
        chip.Width = label.PreferredWidth + 40;

        var x = new Button
        {
            Text      = "✕",
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = TextDim,
            BackColor = Color.White,
            Size      = new Size(20, 20),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Location  = new Point(chip.Width - 24, 4)
        };
        x.FlatAppearance.BorderSize = 0;
        new ToolTip().SetToolTip(x, "Remove this attachment");
        x.Click += (_, _) =>
        {
            _set.Remove(a.FullPath);
            RefreshChips();
        };
        chip.Controls.Add(x);

        // Subtle border via Paint — cheaper than a custom panel class.
        chip.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(220, 220, 220));
            var r = chip.ClientRectangle;
            e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
        };

        return chip;
    }

    private void OnAttachClicked()
    {
        using var ofd = new OpenFileDialog
        {
            Title       = "Attach file to your ticket",
            Multiselect = true,
            Filter      =
                "Common files (images, PDFs, docs)|" +
                "*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.heic;*.pdf;*.txt;*.log;*.csv;" +
                "*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.zip|" +
                "All files (*.*)|*.*"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        AddPaths(ofd.FileNames);
    }

    /// <summary>
    /// Take whatever's on the clipboard and try to add it as an attachment.
    /// Priority: bitmap (Win+Shift+S, screenshots, "Copy image") → file drop
    /// (a real file copied in Explorer) → fail gracefully.
    /// </summary>
    private void OnPasteClicked()
    {
        try
        {
            // 1. Image data (most common case — Snip & Sketch leaves the
            //    captured area on the clipboard as a bitmap).
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img == null)
                {
                    Warn("Couldn't read the image from the clipboard.");
                    return;
                }

                // Persist to %TEMP% as PNG. We use a deterministic prefix so
                // CleanupClipboardTempFiles can find any leftovers easily.
                string tempPath = Path.Combine(
                    Path.GetTempPath(),
                    $"HelpdeskHeroes_clip_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");
                try
                {
                    img.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception ex)
                {
                    Warn($"Couldn't save the pasted image: {ex.Message}");
                    return;
                }

                if (!_set.TryAdd(tempPath, isClipboardCapture: true, out string addError))
                {
                    // Roll back the temp file we just wrote — TryAdd's
                    // rejection means we're not going to use it.
                    try { File.Delete(tempPath); } catch { }
                    Warn(addError);
                    return;
                }
                RefreshChips();
                return;
            }

            // 2. File drop — user copied a file in Explorer.
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var paths = new string[files.Count];
                for (int i = 0; i < files.Count; i++) paths[i] = files[i] ?? "";
                AddPaths(paths);
                return;
            }

            Warn("The clipboard doesn't have an image or file on it. " +
                 "Try Win + Shift + S to snip your screen first, then click Paste.");
        }
        catch (Exception ex)
        {
            // Clipboard reads can throw ExternalException ("Requested clipboard
            // operation did not succeed") if another app is holding it. Tell
            // the user to retry.
            Warn($"Couldn't read the clipboard: {ex.Message}. Try again in a second.");
        }
    }

    private void AddPaths(System.Collections.Generic.IEnumerable<string> paths)
    {
        var failures = new System.Collections.Generic.List<string>();
        bool addedAny = false;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (_set.TryAdd(p, out string err))
            {
                addedAny = true;
            }
            else
            {
                failures.Add(err);
            }
        }
        if (addedAny) RefreshChips();
        if (failures.Count > 0) Warn(string.Join("\r\n\r\n", failures));
    }

    private void Warn(string message)
    {
        MessageBox.Show(this, message, "Attachment",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, Math.Max(1, max - 1)) + "…";
    }
}
