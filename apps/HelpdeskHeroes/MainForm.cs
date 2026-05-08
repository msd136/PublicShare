using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HelpdeskHeroes;

public sealed class MainForm : Form
{
    // TODO: Replace with your organization's FAQ / knowledge-base URL.
    private const string FaqUrl        = "https://example.com/helpdesk-faqs";

    private static readonly Color BrandBlue   = Color.FromArgb(0, 90, 158);
    private static readonly Color ButtonBlue  = Color.FromArgb(0, 120, 212);
    private static readonly Color ButtonHover = Color.FromArgb(0, 99, 177);

    public MainForm()
    {
        Text             = "Helpdesk Heroes";
        StartPosition    = FormStartPosition.CenterScreen;
        FormBorderStyle  = FormBorderStyle.FixedDialog;
        MaximizeBox      = false;
        MinimizeBox      = false;
        ClientSize       = new Size(500, 420);
        BackColor        = Color.White;
        Font             = new Font("Segoe UI", 10F);
        KeyPreview       = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Branding.Apply(this);

        var titleLabel = new Label
        {
            Text       = "Helpdesk Heroes",
            Font       = new Font("Segoe UI Semibold", 22F, FontStyle.Bold),
            ForeColor  = BrandBlue,
            AutoSize   = false,
            TextAlign  = ContentAlignment.MiddleCenter,
            Dock       = DockStyle.Top,
            Height     = 60
        };

        var subtitleLabel = new Label
        {
            Text       = "How can we help you today?",
            Font       = new Font("Segoe UI", 11F),
            ForeColor  = Color.DimGray,
            AutoSize   = false,
            TextAlign  = ContentAlignment.MiddleCenter,
            Dock       = DockStyle.Top,
            Height     = 30
        };

        var emailBtn = MakeBigButton(
            "✉  Email the Helpdesk",
            "Type one line about what's wrong — we'll grab everything else and send it for you.");
        emailBtn.Click += (_, _) => QuickEmailFlow();

        var faqBtn = MakeBigButton(
            "📚  Browse the FAQs",
            "Quick answers to common questions.");
        faqBtn.Click += (_, _) => OpenFaqs();

        var notSureBtn = MakeBigButton(
            "🦸  Get Help",
            "Walk through a few quick questions; we'll auto-send the helpdesk a complete ticket.");
        notSureBtn.Click += (_, _) => ShowGetHelp();

        var stack = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(24, 8, 24, 24),
            BackColor   = Color.White
        };
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        stack.Controls.Add(emailBtn,   0, 0);
        stack.Controls.Add(notSureBtn, 0, 1);
        stack.Controls.Add(faqBtn,     0, 2);

        // Add Fill control FIRST so docked Top controls layer on top of it correctly.
        Controls.Add(stack);
        Controls.Add(subtitleLabel);
        Controls.Add(titleLabel);
    }

    private static Button MakeBigButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Text      = text,
            Dock      = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            BackColor = ButtonBlue,
            ForeColor = Color.White,
            Margin    = new Padding(0, 6, 0, 6),
            Cursor    = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize          = 0;
        btn.FlatAppearance.MouseOverBackColor  = ButtonHover;
        btn.FlatAppearance.MouseDownBackColor  = BrandBlue;
        new ToolTip().SetToolTip(btn, tooltip);
        return btn;
    }

    // -------- Button actions --------

    /// <summary>
    /// One-line ticket flow: prompts for a brief description, then auto-sends
    /// it with the full system / browser / dialog snapshot attached. Used by
    /// the "Email the Helpdesk" button when the user knows what's wrong
    /// and doesn't need the full wizard.
    /// </summary>
    private async void QuickEmailFlow()
    {
        // Build the report up front so the QuickIssueDialog's attachment bar
        // has a stable AttachmentSet to write to. If the user cancels, we
        // still want to clean up any clipboard-paste temp files they staged.
        var report = new TroubleshootingReport { Category = "Quick ticket" };

        try
        {
            using var prompt = new QuickIssueDialog(report.Attachments);
            if (prompt.ShowDialog(this) != DialogResult.OK)
            {
                report.Attachments.CleanupClipboardTempFiles();
                return;
            }

            string oneLine = prompt.IssueText.Trim();
            if (oneLine.Length == 0) oneLine = "(no description provided)";
            report.FreeText = oneLine;

            // Capture screen state at click-time, not later — same intent as the
            // wizard's "Look at my screen" button.
            report.RefreshScreenSnapshot(manual: true);

            string subject  = report.BuildSubject();
            string textBody = report.BuildBody();
            string htmlBody = report.BuildHtmlBody();

            // Show a tiny "Sending…" modal so the user knows something is
            // happening — without it, the UI would freeze for up to ~15s on a
            // bad connection (HttpClient timeout) and feel broken.
            using var sending = new SendingDialog();
            sending.Show(this);
            Enabled = false;

            EmailSender.SendResult result;
            try
            {
                result = await EmailSender.SendAsync(
                    Configuration.HelpdeskRecipient, subject, textBody, htmlBody, report.Attachments)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = new EmailSender.SendResult(
                    EmailSender.SendStatus.Failed,
                    $"Unexpected error during send: {ex.Message}");
            }
            finally
            {
                Enabled = true;
                sending.Close();
            }

            ShowSendResult(result);
        }
        finally
        {
            // Always tidy up clipboard-paste temp files — both on success
            // (we've already encoded the bytes into the email) and on
            // failure (we don't keep stale screenshots in %TEMP%).
            report.Attachments.CleanupClipboardTempFiles();
        }
    }

    private void ShowSendResult(EmailSender.SendResult result)
    {
        switch (result.Status)
        {
            case EmailSender.SendStatus.Sent:
                MessageBox.Show(this,
                    "🎉  Sent to the helpdesk!\r\n\r\n" +
                    "Your ticket is on its way. They'll reply to your email.",
                    "Helpdesk Heroes",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;

            default:
                MessageBox.Show(this,
                    $"We couldn't send your ticket.\r\n\r\n" +
                    $"Please email {Configuration.HelpdeskRecipient} directly.\r\n\r\n" +
                    $"Details: {result.ErrorDetail}",
                    "Helpdesk Heroes",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
        }
    }

    private void OpenFaqs()
    {
        // Strict Edge per deployment policy. EdgeLauncher handles the
        // "Edge isn't installed" path with a copy-friendly URL dialog.
        EdgeLauncher.Open(FaqUrl, this);
    }

    private void ShowGetHelp()
    {
        using var dlg = new GetHelpForm();
        dlg.ShowDialog(this);
    }
}

/// <summary>
/// Tiny modal that asks the user for a one-line description of what's
/// wrong before the quick-email flow auto-sends the ticket. Matches the rest
/// of the app's branding so it doesn't feel like a Windows InputBox. Hosts
/// an <see cref="AttachmentBar"/> docked at the bottom so the user can
/// attach a screenshot from this flow too — the bar is bound to the same
/// AttachmentSet the caller passes in, so the staged files flow through to
/// the email send.
/// </summary>
internal sealed class QuickIssueDialog : Form
{
    private static readonly Color BrandBlue   = Color.FromArgb(0, 90, 158);
    private static readonly Color ButtonBlue  = Color.FromArgb(0, 120, 212);
    private static readonly Color ButtonHover = Color.FromArgb(0, 99, 177);
    private static readonly Color SoftGrey    = Color.FromArgb(245, 247, 250);

    private readonly TextBox _issueBox;

    public string IssueText => _issueBox.Text;

    public QuickIssueDialog(AttachmentSet attachments)
    {
        Text            = "Quick ticket";
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        // 280 (original) + 64 (attachment bar) = 344
        ClientSize      = new Size(520, 344);
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 10F);
        KeyPreview      = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        Branding.Apply(this);

        var header = new Label
        {
            Text      = "✉  What's wrong?",
            Font      = new Font("Segoe UI Semibold", 14F, FontStyle.Bold),
            ForeColor = BrandBlue,
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock      = DockStyle.Top,
            Height    = 48,
            Padding   = new Padding(20, 0, 20, 0),
            BackColor = SoftGrey
        };

        var blurb = new Label
        {
            Text      = "Tell us in a sentence or two — we'll attach your computer name, " +
                        "open apps, and what's on your screen automatically, then send it.",
            Font      = new Font("Segoe UI", 9.5F),
            ForeColor = Color.DimGray,
            AutoSize  = false,
            Dock      = DockStyle.Top,
            Height    = 50,
            Padding   = new Padding(20, 8, 20, 4)
        };

        _issueBox = new TextBox
        {
            Multiline   = true,
            ScrollBars  = ScrollBars.Vertical,
            Font        = new Font("Segoe UI", 10F),
            BorderStyle = BorderStyle.FixedSingle,
            Width       = 470,
            Height      = 90
        };
        _issueBox.PlaceholderText =
            "e.g. Word keeps crashing when I open my report";

        var inputHost = new Panel
        {
            Dock      = DockStyle.Fill,
            Padding   = new Padding(20, 4, 20, 8),
            BackColor = Color.White
        };
        _issueBox.Dock = DockStyle.Fill;
        inputHost.Controls.Add(_issueBox);

        // Buttons
        var sendBtn = new Button
        {
            Text      = "Send to helpdesk now ✉",
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonBlue,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Height    = 38,
            Width     = 220,
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        sendBtn.FlatAppearance.BorderSize         = 0;
        sendBtn.FlatAppearance.MouseOverBackColor = ButtonHover;
        sendBtn.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancelBtn = new Button
        {
            Text      = "Cancel",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = BrandBlue,
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Height    = 38,
            Width     = 100,
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        cancelBtn.FlatAppearance.BorderColor = BrandBlue;
        cancelBtn.FlatAppearance.BorderSize  = 1;
        cancelBtn.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var btnRow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height        = 60,
            Padding       = new Padding(20, 12, 20, 12),
            BackColor     = SoftGrey
        };
        btnRow.Controls.Add(sendBtn);
        btnRow.Controls.Add(cancelBtn);

        // Attachment bar — bound to the AttachmentSet the caller passed in,
        // so files staged here flow through to QuickEmailFlow's send call.
        var attachBar = new AttachmentBar(attachments) { Dock = DockStyle.Bottom };

        // Add Fill first, then docked edges. The button row should be at
        // the very bottom; attachment bar above it; header/blurb at the top.
        // For two bottom-docked controls, the LATER-added one wins the edge,
        // so we add attachBar BEFORE btnRow.
        Controls.Add(inputHost);
        Controls.Add(attachBar);
        Controls.Add(btnRow);
        Controls.Add(blurb);
        Controls.Add(header);

        AcceptButton = sendBtn;
        CancelButton = cancelBtn;
    }
}

/// <summary>
/// Tiny in-flight indicator shown while the SMTP2GO POST is pending in
/// the QuickEmailFlow path. Kept deliberately lightweight — the wizard
/// flow has its own SendingPage; this is just for the one-line ticket.
/// </summary>
internal sealed class SendingDialog : Form
{
    private static readonly Color BrandBlue = Color.FromArgb(0, 90, 158);

    public SendingDialog()
    {
        Text            = "Sending…";
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox      = false;
        ShowInTaskbar   = false;
        ClientSize      = new Size(360, 110);
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 10F);
        Branding.Apply(this);

        var label = new Label
        {
            Text      = "📤  Sending your ticket to the helpdesk…",
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = BrandBlue,
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Top,
            Height    = 50
        };

        var bar = new ProgressBar
        {
            Style                 = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Dock                  = DockStyle.Top,
            Height                = 14,
            Margin                = new Padding(20, 0, 20, 0)
        };

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12 };

        // Add Fill placeholder, then docked Top controls in reverse order
        // so layout stacks correctly.
        Controls.Add(new Panel { Dock = DockStyle.Fill });
        Controls.Add(bar);
        Controls.Add(spacer);
        Controls.Add(label);
    }
}
