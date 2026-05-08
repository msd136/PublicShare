using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HelpdeskHeroes;

/// <summary>
/// Multi-step troubleshooting wizard for users. Replaces the older
/// single-page question dialog.
///
/// Flow:
///   CategoryPage  →  one of six per-category pages  →  WrapUpPage  →  auto-send
///   (any page can also branch to ResolvedPage via the "It's working now" button)
///
/// Cross-cutting features:
///   - Always-visible "URGENT — blocking my work" toggle in the footer.
///   - Self-service tips on each page have a "✓ I tried this" check that
///     gets folded into the email's "Already tried" section automatically.
///   - Auto-collected device info (computer name, user, Windows version, Wi-Fi
///     SSID, timestamp, app version, open apps, browser tabs, foreground
///     window + visible dialogs) is appended to the email body silently.
///   - On send: the wizard does NOT pop Outlook — it drives Outlook via COM
///     and calls .Send() so the message leaves immediately, with no further
///     action required from the user.
/// </summary>
public sealed class GetHelpForm : Form
{
    // TODO: Replace with your organization's FAQ / knowledge-base URL.
    private const string FaqUrl        = "https://example.com/helpdesk-faqs";

    internal static readonly Color BrandBlue   = Color.FromArgb(0, 90, 158);
    internal static readonly Color ButtonBlue  = Color.FromArgb(0, 120, 212);
    internal static readonly Color ButtonHover = Color.FromArgb(0, 99, 177);
    internal static readonly Color SoftGrey    = Color.FromArgb(245, 247, 250);
    internal static readonly Color TextDim     = Color.FromArgb(90, 90, 90);

    private readonly TroubleshootingReport _report = new();
    private readonly Panel _contentPanel;
    private readonly Label _titleLabel;
    private readonly CheckBox _urgentCheck;
    private readonly Button _backBtn;
    private readonly AttachmentBar _attachmentBar;
    private readonly Stack<Func<WizardPage>> _history = new();
    private WizardPage? _currentPage;

    internal TroubleshootingReport Report => _report;

    public GetHelpForm()
    {
        Text             = "Get Help";
        StartPosition    = FormStartPosition.CenterParent;
        FormBorderStyle  = FormBorderStyle.FixedDialog;
        MaximizeBox      = false;
        MinimizeBox      = false;
        // 580 (original) + 64 (attachment bar) = 644
        ClientSize       = new Size(680, 644);
        BackColor        = Color.White;
        Font             = new Font("Segoe UI", 10F);
        KeyPreview       = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Branding.Apply(this);

        // Clean up any clipboard-paste temp files we created. The wizard owns
        // them — if we don't clean up, %TEMP% accumulates pasted screenshots
        // forever.
        FormClosed += (_, _) => _report.Attachments.CleanupClipboardTempFiles();

        // ---- Header ----
        _titleLabel = new Label
        {
            Text       = "🦸  Get Help",
            Font       = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor  = BrandBlue,
            AutoSize   = false,
            TextAlign  = ContentAlignment.MiddleLeft,
            Dock       = DockStyle.Top,
            Height     = 56,
            Padding    = new Padding(20, 0, 20, 0),
            BackColor  = SoftGrey
        };

        // ---- Footer ----
        var footer = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 56,
            BackColor = SoftGrey
        };

        _backBtn = new Button
        {
            Text      = "← Back",
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 9F),
            BackColor = Color.White,
            ForeColor = Color.Black,
            Size      = new Size(80, 32),
            Location  = new Point(16, 12),
            Cursor    = Cursors.Hand,
            Visible   = false,
            UseVisualStyleBackColor = false
        };
        _backBtn.FlatAppearance.BorderColor = Color.Silver;
        _backBtn.Click += (_, _) => GoBack();

        _urgentCheck = new CheckBox
        {
            Text      = "⚠ This is blocking my work",
            Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 60, 30),
            AutoSize  = true,
            Location  = new Point(112, 18),
            BackColor = SoftGrey,
            Cursor    = Cursors.Hand
        };
        new ToolTip().SetToolTip(_urgentCheck,
            "Check if you can't keep working on your assignment — adds [URGENT] to the subject.");
        _urgentCheck.CheckedChanged += (_, _) => _report.IsUrgent = _urgentCheck.Checked;

        footer.Controls.Add(_backBtn);
        footer.Controls.Add(_urgentCheck);

        // ---- Content ----
        _contentPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.White,
            Padding   = new Padding(0)
        };

        // ---- Attachment bar ----
        // Lives between the content area and the urgent/back footer so it
        // persists across page navigation. The bar is bound directly to
        // the report's AttachmentSet, so adds/removes from any page (or
        // from the QuickIssueDialog flow elsewhere) all show up here.
        _attachmentBar = new AttachmentBar(_report.Attachments)
        {
            Dock = DockStyle.Bottom
        };

        // Add Fill first, then docked edges. For two controls on the same
        // side, the LATER-added control claims the edge (it has higher
        // z-order, and the dock layout walks in reverse z-order). So we
        // add the attachment bar BEFORE the urgent footer — the footer
        // ends up pinned to the very bottom, with the attachment bar
        // sitting inside (above) it.
        Controls.Add(_contentPanel);
        Controls.Add(_attachmentBar);
        Controls.Add(footer);
        Controls.Add(_titleLabel);

        // Boot the first page
        Navigate(() => new CategoryPage(this), pushHistory: false);
    }

    /// <summary>
    /// Force-refresh the attachment bar. Pages that programmatically modify
    /// <see cref="TroubleshootingReport.Attachments"/> (rare — usually the
    /// bar's own buttons handle changes) should call this to repaint the
    /// chip row.
    /// </summary>
    internal void RefreshAttachmentBar() => _attachmentBar.RefreshChips();

    /// <summary>Swap content to a new page. Pass pushHistory=false for the very first page.</summary>
    internal void Navigate(Func<WizardPage> factory, bool pushHistory = true)
    {
        if (pushHistory && _currentPage != null)
        {
            // Capture the factory that produced the current page so Back can recreate it.
            var snapshot = _currentPage.Factory;
            if (snapshot != null) _history.Push(snapshot);
        }

        var next = factory();
        next.Factory = factory;

        _contentPanel.SuspendLayout();
        _contentPanel.Controls.Clear();
        _currentPage?.Dispose();
        next.Dock = DockStyle.Fill;
        _contentPanel.Controls.Add(next);
        _contentPanel.ResumeLayout();

        _currentPage = next;
        _titleLabel.Text = "🦸  " + next.Title;
        _backBtn.Visible = _history.Count > 0;

        // Hide the attachment bar on terminal pages (Sending / Sent / Resolved) —
        // there's nothing sensible the user can do with it after the email
        // is in flight or the issue self-resolved. Pages opt-out by setting
        // HidesAttachmentBar to true.
        _attachmentBar.Visible = !next.HidesAttachmentBar;
    }

    private void GoBack()
    {
        if (_history.Count == 0) return;
        var prev = _history.Pop();
        // Don't push current onto history when navigating back.
        Navigate(prev, pushHistory: false);
    }

    /// <summary>
    /// Build the report and auto-send via the SMTP2GO API. The user does
    /// NOT have to confirm or click Send — this is the explicit per-spec
    /// behaviour for the helpdesk app: gather everything, send it, show a
    /// confirmation. The send runs on a background thread so the UI stays
    /// responsive while we wait on the network.
    /// </summary>
    internal async void SendHelpdeskEmail()
    {
        // Make sure the urgent toggle is reflected even if the user toggled it last.
        _report.IsUrgent = _urgentCheck.Checked;

        // Build the body on the UI thread. BuildBody() reaches into UI
        // Automation (BrowserTabs / ActiveContext / OpenApps) which is
        // STA-affine — moving it to a thread-pool thread risks deadlocks
        // and inconsistent reads. The collection passes are individually
        // bounded (CollectTimeoutMs in each module) so worst-case this
        // takes ~12 s before the SendingPage even appears. That's the
        // existing behaviour and not made worse by this change.
        //
        // We build BOTH the plain-text and HTML versions: the HTML body
        // is what the helpdesk's Outlook actually renders, and the plain
        // text is the failure-mode "copy this manually" payload shown on
        // SentPage when the send doesn't go through. Building both here
        // avoids re-running the (slow) UIA passes in BuildHtmlBody — each
        // collection module caches its result for the lifetime of one
        // wizard run, so the second build is essentially free.
        string subject  = _report.BuildSubject();
        string textBody = _report.BuildBody();
        string htmlBody = _report.BuildHtmlBody();

        // Show an in-flight page while the HTTP call is pending. SMTP2GO
        // is normally sub-second, but we shouldn't trust the network on a
        // user laptop — and freezing the UI during a 15 s timeout would
        // look broken.
        Navigate(() => new SendingPage(this), pushHistory: false);

        EmailSender.SendResult result;
        try
        {
            result = await EmailSender.SendAsync(
                Configuration.HelpdeskRecipient, subject, textBody, htmlBody, _report.Attachments)
                .ConfigureAwait(true); // back to UI thread
        }
        catch (Exception ex)
        {
            // SendAsync swallows everything internally, but belt-and-braces:
            // if anything escapes we still want a clean SentPage render
            // rather than an unhandled exception toasting the wizard.
            result = new EmailSender.SendResult(
                EmailSender.SendStatus.Failed,
                $"Unexpected error during send: {ex.Message}");
        }

        if (IsDisposed) return;
        Navigate(() => new SentPage(this, result, subject, textBody), pushHistory: false);
    }

    internal static void OpenUrl(string url)
    {
        // Strict Edge per deployment policy — see EdgeLauncher for the reasoning.
        // We pass null for the owner here because Form.ActiveForm gets us the
        // right modal parent in the common case, and EdgeLauncher's missing-Edge
        // dialog handles a null owner gracefully.
        EdgeLauncher.Open(url, Form.ActiveForm);
    }

    internal static string FaqLink     => FaqUrl;
    internal static string HelpdeskTo  => Configuration.HelpdeskRecipient;
}

// ====================================================================
// Wizard page base
// ====================================================================

internal abstract class WizardPage : UserControl
{
    protected GetHelpForm Host { get; }
    public abstract string Title { get; }

    /// <summary>
    /// Pages where attaching files no longer makes sense — terminal pages
    /// like Sending / Sent / Resolved — override this to return true so
    /// <see cref="GetHelpForm"/> hides the attachment bar while they're shown.
    /// </summary>
    public virtual bool HidesAttachmentBar => false;

    /// <summary>Set by GetHelpForm.Navigate() so Back can recreate this page.</summary>
    internal Func<WizardPage>? Factory { get; set; }

    protected WizardPage(GetHelpForm host)
    {
        Host = host;
        BackColor  = Color.White;
        Padding    = new Padding(20, 16, 20, 12);
        AutoScroll = true;
    }

    // ---- Layout helpers used by every concrete page ----

    protected static Label MakeIntro(string text) => new()
    {
        Text       = text,
        Font       = new Font("Segoe UI", 10.5F),
        ForeColor  = GetHelpForm.TextDim,
        AutoSize   = false,
        Width      = 620,
        Height     = 44,
        TextAlign  = ContentAlignment.TopLeft,
        Margin     = new Padding(0, 0, 0, 8)
    };

    protected static Label MakeQuestion(string text) => new()
    {
        Text     = text,
        Font     = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
        ForeColor = Color.Black,
        AutoSize = true,
        Margin   = new Padding(0, 8, 0, 4)
    };

    protected static Panel MakeRadioGroup(string[] options, out RadioButton[] radios)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Margin        = new Padding(8, 0, 0, 4),
            BackColor     = Color.Transparent
        };
        radios = options.Select(o => new RadioButton
        {
            Text     = o,
            AutoSize = true,
            Font     = new Font("Segoe UI", 10F),
            Margin   = new Padding(0, 2, 0, 2)
        }).ToArray();
        foreach (var r in radios) panel.Controls.Add(r);
        return panel;
    }

    protected static TextBox MakeTextBox(int height = 28, bool multiline = false)
    {
        return new TextBox
        {
            Multiline  = multiline,
            Width      = 580,
            Height     = height,
            Font       = new Font("Segoe UI", 10F),
            Margin     = new Padding(8, 0, 0, 8),
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
        };
    }

    /// <summary>
    /// "Try this" row: a check box that says "I tried this" once clicked,
    /// auto-records the step into the report's AlreadyTried list. Optional
    /// inline link runs an action (e.g. opens a URL).
    /// </summary>
    protected Panel MakeTryThis(string label, string? linkText = null, Action? linkAction = null)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Margin        = new Padding(8, 2, 0, 2),
            BackColor     = Color.Transparent
        };

        var check = new CheckBox
        {
            Text      = "I tried this",
            AutoSize  = true,
            Font      = new Font("Segoe UI", 9F),
            ForeColor = GetHelpForm.TextDim,
            Margin    = new Padding(0, 4, 8, 0)
        };
        check.CheckedChanged += (_, _) =>
        {
            if (check.Checked && !Host.Report.AlreadyTried.Contains(label))
                Host.Report.AlreadyTried.Add(label);
            else if (!check.Checked)
                Host.Report.AlreadyTried.Remove(label);
        };

        var lbl = new Label
        {
            Text        = label,
            AutoSize    = true,
            Font        = new Font("Segoe UI", 10F),
            Margin      = new Padding(0, 4, 6, 0),
            MaximumSize = new Size(440, 0)
        };

        row.Controls.Add(check);
        row.Controls.Add(lbl);

        if (!string.IsNullOrEmpty(linkText) && linkAction != null)
        {
            var link = new LinkLabel
            {
                Text      = linkText,
                AutoSize  = true,
                Font      = new Font("Segoe UI", 9F),
                LinkColor = GetHelpForm.BrandBlue,
                Margin    = new Padding(0, 4, 0, 0)
            };
            link.Click += (_, _) => linkAction();
            row.Controls.Add(link);
        }

        return row;
    }

    protected Button MakePrimaryButton(string text)
    {
        var b = new Button
        {
            Text      = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = GetHelpForm.ButtonBlue,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Height    = 40,
            Width     = 240,
            Cursor    = Cursors.Hand,
            Margin    = new Padding(8, 12, 8, 0),
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize         = 0;
        b.FlatAppearance.MouseOverBackColor = GetHelpForm.ButtonHover;
        return b;
    }

    protected Button MakeSecondaryButton(string text)
    {
        var b = new Button
        {
            Text      = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = GetHelpForm.BrandBlue,
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Height    = 40,
            Width     = 200,
            Cursor    = Cursors.Hand,
            Margin    = new Padding(8, 12, 8, 0),
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderColor       = GetHelpForm.BrandBlue;
        b.FlatAppearance.BorderSize        = 1;
        b.FlatAppearance.MouseOverBackColor = GetHelpForm.SoftGrey;
        return b;
    }

    protected static FlowLayoutPanel MakeBody()
    {
        return new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Dock          = DockStyle.Top,
            BackColor     = Color.Transparent
        };
    }

    protected static string SelectedRadio(RadioButton[] radios)
        => radios.FirstOrDefault(r => r.Checked)?.Text ?? "";

    protected static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, Math.Max(1, max - 1)) + "…";
    }
}

// ====================================================================
// 1. Category picker
// ====================================================================

internal sealed class CategoryPage : WizardPage
{
    public override string Title => "What's going on?";

    public CategoryPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "Pick the closest match. We'll ask a couple of quick questions and " +
            "send a helpdesk ticket with the answers filled in for you. " +
            "You don't need to do anything else — we'll send it right when you're done."));

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount    = 3,
            AutoSize    = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin      = new Padding(0, 8, 0, 0),
            BackColor   = Color.Transparent
        };
        for (int i = 0; i < 2; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));

        Add(grid, 0, 0, "🔑  Password / login",
            "Locked out, expired, or won't accept it",
            () => Host.Navigate(() => new PasswordPage(Host)));
        Add(grid, 1, 0, "🌐  Website blocked",
            "Site is blocked, won't load, or shows an error",
            () => Host.Navigate(() => new WebsitePage(Host)));
        Add(grid, 0, 1, "🖥  An app",
            "Word, Chrome, Teams, Canvas, etc.",
            () => Host.Navigate(() => new DesktopAppPage(Host)));
        Add(grid, 1, 1, "🖱  Mouse / keyboard",
            "Not working, lagging, or disconnected",
            () => Host.Navigate(() => new MouseKeyboardPage(Host)));
        Add(grid, 0, 2, "✉  Email",
            "Send / receive trouble or stuck messages",
            () => Host.Navigate(() => new EmailPage(Host)));
        Add(grid, 1, 2, "❓  Something else",
            "Tell us in your own words",
            () => Host.Navigate(() => new OtherPage(Host)));

        body.Controls.Add(grid);
        Controls.Add(body);
    }

    private void Add(TableLayoutPanel grid, int col, int row,
                     string title, string sub, Action onClick)
    {
        var btn = new Button
        {
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Text      = $"{title}\r\n     {sub}",
            BackColor = GetHelpForm.ButtonBlue,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Width     = 290,
            Height    = 70,
            Margin    = new Padding(4),
            Cursor    = Cursors.Hand,
            Padding   = new Padding(12, 8, 12, 8),
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize         = 0;
        btn.FlatAppearance.MouseOverBackColor = GetHelpForm.ButtonHover;
        btn.Click += (_, _) =>
        {
            // Strip the leading emoji + spaces for a clean category label in the email.
            int firstSpace = title.IndexOf(' ');
            Host.Report.Category = firstSpace < 0
                ? title
                : title.Substring(firstSpace + 1).Trim();
            onClick();
        };
        grid.Controls.Add(btn, col, row);
    }
}

// ====================================================================
// 2. Password
// ====================================================================

internal sealed class PasswordPage : WizardPage
{
    public override string Title => "Password / login issue";

    public PasswordPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "If your password just expired and you still know what it is, you " +
            "can change it yourself in about thirty seconds. If you're locked " +
            "out or forgot it, the helpdesk has to reset it — fill in the " +
            "questions and we'll send the email automatically."));

        // Q1
        body.Controls.Add(MakeQuestion("What's happening?"));
        var q1Panel = MakeRadioGroup(new[]
        {
            "Locked out (too many wrong tries)",
            "I forgot it",
            "It expired or is asking me to change it",
            "It works on the website but not on my laptop"
        }, out var q1);
        body.Controls.Add(q1Panel);

        // Q2
        body.Controls.Add(MakeQuestion("When did it last work?"));
        var q2Panel = MakeRadioGroup(new[]
        {
            "Today", "Yesterday", "Earlier this week", "I can't remember"
        }, out var q2);
        body.Controls.Add(q2Panel);

        // Self-service
        body.Controls.Add(MakeQuestion("If your password just expired — try this first"));
        var ctrlAltDelTip = new Label
        {
            Text = "Press Ctrl + Alt + Delete, choose \"Change a password,\" type your " +
                   "current password, then a new one twice. This works as long as you " +
                   "still know the current password.",
            Font      = new Font("Segoe UI", 9.5F),
            ForeColor = GetHelpForm.TextDim,
            AutoSize  = false,
            Width     = 600,
            Height    = 56,
            Margin    = new Padding(8, 0, 0, 4)
        };
        body.Controls.Add(ctrlAltDelTip);
        body.Controls.Add(MakeTryThis("Changed it via Ctrl + Alt + Del → Change a password"));

        body.Controls.Add(MakeQuestion("Other things to check"));
        body.Controls.Add(MakeTryThis("Made sure Caps Lock is off"));
        body.Controls.Add(MakeTryThis("Retyped slowly — no copy/paste"));

        // Action row
        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var workingBtn = MakeSecondaryButton("✓  It's working now");
        workingBtn.Click += (_, _) => Host.Navigate(() => new ResolvedPage(Host));

        var continueBtn = MakePrimaryButton("Next →");
        continueBtn.Click += (_, _) =>
        {
            Host.Report.AddAnswer("Symptom",     SelectedRadio(q1));
            Host.Report.AddAnswer("Last worked", SelectedRadio(q2));
            Host.Navigate(() => new WrapUpPage(Host));
        };

        actions.Controls.Add(workingBtn);
        actions.Controls.Add(continueBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// 3. Website / content filter
// ====================================================================

internal sealed class WebsitePage : WizardPage
{
    public override string Title => "Website blocked or broken";

    public WebsitePage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "Tell us what site and exactly what you're seeing. " +
            "If it's blocked by the network filter, we may need to add an exception."));

        body.Controls.Add(MakeQuestion("What's happening?"));
        var q1Panel = MakeRadioGroup(new[]
        {
            "Site is blocked (filter page shown)",
            "Won't load at all",
            "Security or certificate warning",
            "Loads but doesn't work right"
        }, out var q1);
        body.Controls.Add(q1Panel);

        body.Controls.Add(MakeQuestion("What's the URL? (paste or type the full address)"));
        var url = MakeTextBox();
        body.Controls.Add(url);

        body.Controls.Add(MakeQuestion("What's the exact error or message you see?"));
        var err = MakeTextBox(60, multiline: true);
        body.Controls.Add(err);

        body.Controls.Add(MakeQuestion("What are you trying to get done? (optional)"));
        var classFor = MakeTextBox();
        classFor.PlaceholderText = "e.g. submitting an expense report by 5pm";
        body.Controls.Add(classFor);

        body.Controls.Add(MakeQuestion("Have you tried these?"));
        body.Controls.Add(MakeTryThis("Tried in the other browser (Edge / Chrome)"));
        body.Controls.Add(MakeTryThis("Closed and reopened the browser"));
        body.Controls.Add(MakeTryThis("Checked the URL for typos"));

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var workingBtn = MakeSecondaryButton("✓  It's working now");
        workingBtn.Click += (_, _) => Host.Navigate(() => new ResolvedPage(Host));

        var continueBtn = MakePrimaryButton("Next →");
        continueBtn.Click += (_, _) =>
        {
            Host.Report.AddAnswer("Symptom",  SelectedRadio(q1));
            Host.Report.AddAnswer("URL",      url.Text);
            Host.Report.AddAnswer("Error",    err.Text);
            Host.Report.AddAnswer("Context",  classFor.Text);
            Host.Navigate(() => new WrapUpPage(Host));
        };

        actions.Controls.Add(workingBtn);
        actions.Controls.Add(continueBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// 4. Desktop app
// ====================================================================

internal sealed class DesktopAppPage : WizardPage
{
    public override string Title => "An app";

    private const string OtherAppOption = "Other (type below)…";

    public DesktopAppPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "Most app trouble is solved by closing it completely and reopening, " +
            "or by a quick restart. Try those first if you haven't."));

        // Pull the live list of apps the user has open right now and offer
        // them as a dropdown, so they don't have to remember the exact name.
        // Frozen apps (Process.Responding == false) are flagged inline so the
        // user sees "Word (not responding)" without having to dig.
        body.Controls.Add(MakeQuestion("Which app?"));

        var detected = OpenApps.Collect();

        var appCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 320,
            Font          = new Font("Segoe UI", 10F),
            Margin        = new Padding(8, 0, 0, 4)
        };
        foreach (var a in detected)
        {
            string label = a.IsResponding ? a.DisplayName : $"{a.DisplayName} (not responding)";
            appCombo.Items.Add(label);
        }
        appCombo.Items.Add(OtherAppOption);
        appCombo.SelectedIndex = 0;
        body.Controls.Add(appCombo);

        // Hint label tells the user what they're seeing.
        int hungCount = detected.Count(a => !a.IsResponding);
        string hint;
        if (detected.Count == 0)
        {
            hint = "(couldn't detect open apps — pick \"Other\" and type the name)";
        }
        else if (hungCount > 0)
        {
            hint = $"(picked up {detected.Count} app{(detected.Count == 1 ? "" : "s")}, " +
                   $"{hungCount} not responding — see the force-close button below)";
        }
        else
        {
            hint = $"(picked up {detected.Count} app{(detected.Count == 1 ? "" : "s")} you have open)";
        }
        var detectedHint = new Label
        {
            Text      = hint,
            Font      = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = GetHelpForm.TextDim,
            AutoSize  = true,
            Margin    = new Padding(8, 0, 0, 8)
        };
        body.Controls.Add(detectedHint);

        // Frozen-app rescue: only render the force-close button when the
        // currently-selected app is non-responding.
        var forceCloseRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Margin        = new Padding(8, 0, 0, 8),
            Visible       = false,
            BackColor     = Color.Transparent
        };
        var forceCloseLabel = new Label
        {
            Text      = "This app isn't responding.",
            Font      = new Font("Segoe UI", 9.5F, FontStyle.Italic),
            ForeColor = Color.FromArgb(180, 60, 30),
            AutoSize  = true,
            Margin    = new Padding(0, 8, 8, 0)
        };
        var forceCloseBtn = new Button
        {
            Text      = "Force close + reopen",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(180, 60, 30),
            Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            Height    = 30,
            Width     = 180,
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin    = new Padding(0, 4, 0, 0)
        };
        forceCloseBtn.FlatAppearance.BorderColor = Color.FromArgb(180, 60, 30);
        forceCloseBtn.FlatAppearance.BorderSize  = 1;
        forceCloseRow.Controls.Add(forceCloseLabel);
        forceCloseRow.Controls.Add(forceCloseBtn);
        body.Controls.Add(forceCloseRow);

        OpenApps.App? CurrentDetectedApp()
        {
            int i = appCombo.SelectedIndex;
            return (i >= 0 && i < detected.Count) ? detected[i] : null;
        }

        void RefreshForceCloseRow()
        {
            var sel = CurrentDetectedApp();
            forceCloseRow.Visible = sel != null && !sel.IsResponding;
        }
        appCombo.SelectedIndexChanged += (_, _) => RefreshForceCloseRow();
        RefreshForceCloseRow();

        forceCloseBtn.Click += (_, _) =>
        {
            var sel = CurrentDetectedApp();
            if (sel == null) return;

            var confirm = MessageBox.Show(this,
                $"Force-close {sel.DisplayName}? Any unsaved work in that app will be lost.\r\n\r\n" +
                "We'll try to close it nicely first, then make it stop if it stays stuck.",
                "Force close",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            bool closed = OpenApps.TryForceClose(sel.ProcessId);
            if (!closed)
            {
                MessageBox.Show(this,
                    $"Couldn't close {sel.DisplayName}. The helpdesk can do it remotely — " +
                    "keep going and we'll send them the details.",
                    "Force close",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Host.Report.AlreadyTried.Add($"Tried to force-close {sel.DisplayName} (didn't work)");
                return;
            }

            Host.Report.AlreadyTried.Add($"Force-closed {sel.DisplayName} via Helpdesk Heroes");

            var reopen = MessageBox.Show(this,
                $"{sel.DisplayName} closed. Reopen it now?",
                "Force close",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (reopen == DialogResult.Yes)
            {
                if (OpenApps.TryRelaunch(sel.ProcessName))
                {
                    Host.Report.AlreadyTried.Add($"Reopened {sel.DisplayName}");
                }
            }

            forceCloseRow.Visible = false;
        };

        // Free-text fallback shown only when "Other" is selected.
        var otherBox = MakeTextBox();
        otherBox.PlaceholderText = "Type the app name";
        otherBox.Visible = false;
        body.Controls.Add(otherBox);

        appCombo.SelectedIndexChanged += (_, _) =>
        {
            otherBox.Visible = (appCombo.SelectedItem as string) == OtherAppOption;
        };
        otherBox.Visible = (appCombo.SelectedItem as string) == OtherAppOption;
        if (detected.Count == 0)
        {
            appCombo.SelectedItem = OtherAppOption;
        }

        body.Controls.Add(MakeQuestion("What's happening?"));
        var q2Panel = MakeRadioGroup(new[]
        {
            "Won't open at all",
            "Crashes after opening",
            "Frozen or not responding",
            "Asks me to sign in over and over",
            "Something else"
        }, out var q2);
        body.Controls.Add(q2Panel);

        body.Controls.Add(MakeQuestion("Exact error message (if any)"));
        var err = MakeTextBox(60, multiline: true);
        body.Controls.Add(err);

        body.Controls.Add(MakeQuestion("When did it start?"));
        var q3Panel = MakeRadioGroup(new[]
        {
            "Today", "This week", "Longer than a week"
        }, out var q3);
        body.Controls.Add(q3Panel);

        body.Controls.Add(MakeQuestion("Have you tried these?"));
        body.Controls.Add(MakeTryThis("Closed the app completely and reopened it"));
        body.Controls.Add(MakeTryThis("Restarted the laptop"));
        body.Controls.Add(MakeTryThis(
            "For Office apps: signed out and back in (File → Account → Sign out)"));

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var workingBtn = MakeSecondaryButton("✓  It's working now");
        workingBtn.Click += (_, _) => Host.Navigate(() => new ResolvedPage(Host));

        var continueBtn = MakePrimaryButton("Next →");
        continueBtn.Click += (_, _) =>
        {
            string app = (appCombo.SelectedItem as string) ?? "";
            if (app == OtherAppOption)
            {
                app = otherBox.Text.Trim();
            }
            Host.Report.AddAnswer("App",     app);
            Host.Report.AddAnswer("Symptom", SelectedRadio(q2));
            Host.Report.AddAnswer("Error",   err.Text);
            Host.Report.AddAnswer("Started", SelectedRadio(q3));
            Host.Navigate(() => new WrapUpPage(Host));
        };

        actions.Controls.Add(workingBtn);
        actions.Controls.Add(continueBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// 5. Mouse / keyboard
// ====================================================================

internal sealed class MouseKeyboardPage : WizardPage
{
    public override string Title => "Mouse or keyboard";

    public MouseKeyboardPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "These are usually a cable, a USB port, or batteries. " +
            "It's worth a minute of checks before we send a tech."));

        body.Controls.Add(MakeQuestion("Which device?"));
        var q1Panel = MakeRadioGroup(new[]
        {
            "Mouse", "Keyboard", "Both", "Trackpad", "Stylus / pen", "Touchscreen"
        }, out var q1);
        body.Controls.Add(q1Panel);

        body.Controls.Add(MakeQuestion("Wired or wireless?"));
        var q2Panel = MakeRadioGroup(new[]
        {
            "Wired (USB cable)",
            "Wireless with USB dongle",
            "Bluetooth",
            "Built into the laptop",
            "Not sure"
        }, out var q2);
        body.Controls.Add(q2Panel);

        body.Controls.Add(MakeQuestion("What's it doing?"));
        var q3Panel = MakeRadioGroup(new[]
        {
            "Not working at all",
            "Lagging or skipping",
            "Some buttons / keys don't work",
            "Disconnects randomly"
        }, out var q3);
        body.Controls.Add(q3Panel);

        body.Controls.Add(MakeQuestion("Have you tried these?"));
        body.Controls.Add(MakeTryThis("Tried a different USB port"));
        body.Controls.Add(MakeTryThis("Replaced the batteries (if wireless)"));
        body.Controls.Add(MakeTryThis("Reseated the USB dongle / cable on both ends"));
        body.Controls.Add(MakeTryThis("Restarted the laptop"));

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var workingBtn = MakeSecondaryButton("✓  It's working now");
        workingBtn.Click += (_, _) => Host.Navigate(() => new ResolvedPage(Host));

        var continueBtn = MakePrimaryButton("Next →");
        continueBtn.Click += (_, _) =>
        {
            Host.Report.AddAnswer("Device",     SelectedRadio(q1));
            Host.Report.AddAnswer("Connection", SelectedRadio(q2));
            Host.Report.AddAnswer("Symptom",    SelectedRadio(q3));
            Host.Navigate(() => new WrapUpPage(Host));
        };

        actions.Controls.Add(workingBtn);
        actions.Controls.Add(continueBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// 6. Email
// ====================================================================

internal sealed class EmailPage : WizardPage
{
    public override string Title => "Email";

    public EmailPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "Restarting Outlook fixes a surprising number of email issues. " +
            "If you can, close it fully and reopen before you keep going."));

        body.Controls.Add(MakeQuestion("Where are you having trouble?"));
        var q1Panel = MakeRadioGroup(new[]
        {
            "Outlook on the laptop",
            "Outlook on the web (browser)",
            "Outlook on my phone",
            "All of them"
        }, out var q1);
        body.Controls.Add(q1Panel);

        body.Controls.Add(MakeQuestion("What's happening?"));
        var q2Panel = MakeRadioGroup(new[]
        {
            "Can't send",
            "Can't receive",
            "Both",
            "A specific message is stuck",
            "Attachment is too big"
        }, out var q2);
        body.Controls.Add(q2Panel);

        body.Controls.Add(MakeQuestion("Specific recipient or sender (optional)"));
        var who = MakeTextBox();
        body.Controls.Add(who);

        body.Controls.Add(MakeQuestion("Exact error message (if any)"));
        var err = MakeTextBox(60, multiline: true);
        body.Controls.Add(err);

        body.Controls.Add(MakeQuestion("Have you tried these?"));
        body.Controls.Add(MakeTryThis("Restarted Outlook completely"));
        body.Controls.Add(MakeTryThis("Checked the Outbox for a stuck message"));
        body.Controls.Add(MakeTryThis(
            "For big attachments: uploaded to OneDrive and shared the link instead"));

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var workingBtn = MakeSecondaryButton("✓  It's working now");
        workingBtn.Click += (_, _) => Host.Navigate(() => new ResolvedPage(Host));

        var continueBtn = MakePrimaryButton("Next →");
        continueBtn.Click += (_, _) =>
        {
            Host.Report.AddAnswer("Where",     SelectedRadio(q1));
            Host.Report.AddAnswer("Symptom",   SelectedRadio(q2));
            Host.Report.AddAnswer("Recipient", who.Text);
            Host.Report.AddAnswer("Error",     err.Text);
            Host.Navigate(() => new WrapUpPage(Host));
        };

        actions.Controls.Add(workingBtn);
        actions.Controls.Add(continueBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// 7. Something else
// ====================================================================

internal sealed class OtherPage : WizardPage
{
    public override string Title => "Something else";

    public OtherPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "Tell us what's going on in your own words. The more specific, " +
            "the faster we can help."));

        body.Controls.Add(MakeQuestion("What's happening?"));
        var what = MakeTextBox(120, multiline: true);
        body.Controls.Add(what);

        body.Controls.Add(MakeQuestion("What were you doing when it happened?"));
        var doing = MakeTextBox(60, multiline: true);
        body.Controls.Add(doing);

        body.Controls.Add(MakeQuestion("When did it start?"));
        var q1Panel = MakeRadioGroup(new[]
        {
            "Today", "Yesterday", "This week", "Longer", "Not sure"
        }, out var q1);
        body.Controls.Add(q1Panel);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var continueBtn = MakePrimaryButton("Next →");
        continueBtn.Click += (_, _) =>
        {
            Host.Report.AddAnswer("Description", what.Text);
            Host.Report.AddAnswer("Was doing",   doing.Text);
            Host.Report.AddAnswer("Started",     SelectedRadio(q1));
            Host.Navigate(() => new WrapUpPage(Host));
        };
        actions.Controls.Add(continueBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// Wrap-up — last page before we auto-send
// ====================================================================

internal sealed class WrapUpPage : WizardPage
{
    public override string Title => "Last couple of questions";

    public WrapUpPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();
        body.Controls.Add(MakeIntro(
            "Two quick context questions, then we'll send your ticket — " +
            "no need to confirm in Outlook, we'll handle that."));

        body.Controls.Add(MakeQuestion("Who's affected?"));
        var scopePanel = MakeRadioGroup(new[]
        {
            "Just me",
            "A few of us",
            "Everyone in my area / team"
        }, out var scope);
        body.Controls.Add(scopePanel);

        body.Controls.Add(MakeQuestion(
            "Did anything change today? (optional — new device, room change, recent update prompt)"));
        var changes = MakeTextBox(60, multiline: true);
        body.Controls.Add(changes);

        body.Controls.Add(MakeQuestion("Anything else we should know? (optional)"));
        var extra = MakeTextBox(80, multiline: true);
        body.Controls.Add(extra);

        // "Look at my screen" — re-runs the UIA snapshot so the foreground
        // window / visible dialog list in the email reflects what's actually
        // on screen right now.
        body.Controls.Add(MakeQuestion("Want us to grab what's on screen right now?"));
        var screenRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Margin        = new Padding(8, 0, 0, 4),
            BackColor     = Color.Transparent
        };
        var screenBtn = new Button
        {
            Text      = "👀  Look at my screen",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = GetHelpForm.BrandBlue,
            Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            Height    = 32,
            Width     = 200,
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin    = new Padding(0, 4, 8, 0)
        };
        screenBtn.FlatAppearance.BorderColor = GetHelpForm.BrandBlue;
        screenBtn.FlatAppearance.BorderSize  = 1;
        var screenStatus = new Label
        {
            Text      = "(captures the active window + any error pop-ups — no screenshot taken)",
            Font      = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = GetHelpForm.TextDim,
            AutoSize  = true,
            Margin    = new Padding(0, 10, 0, 0)
        };
        screenBtn.Click += (_, _) =>
        {
            var snap = Host.Report.RefreshScreenSnapshot(manual: true);
            if (!snap.HasAnything)
            {
                screenStatus.Text = "Nothing detected — that's OK, we'll attach what we have.";
            }
            else
            {
                int n = snap.Dialogs.Count;
                string dialogPart = n switch
                {
                    0 => "no error pop-ups visible",
                    1 => "1 error pop-up captured",
                    _ => $"{n} error pop-ups captured"
                };
                string focusPart = string.IsNullOrWhiteSpace(snap.ForegroundTitle)
                    ? snap.ForegroundApp
                    : $"{snap.ForegroundApp} — \"{Truncate(snap.ForegroundTitle, 60)}\"";
                screenStatus.Text = $"✓ Captured: {focusPart} • {dialogPart}.";
                screenStatus.ForeColor = GetHelpForm.BrandBlue;
            }
        };
        screenRow.Controls.Add(screenBtn);
        screenRow.Controls.Add(screenStatus);
        body.Controls.Add(screenRow);

        // Generic FAQ nudge.
        var faqRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Margin        = new Padding(0, 4, 0, 0),
            BackColor     = Color.Transparent
        };
        var faqPrefix = new Label
        {
            Text      = "Before you send — it might already be answered:",
            Font      = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = GetHelpForm.TextDim,
            AutoSize  = true,
            Margin    = new Padding(0, 4, 6, 0)
        };
        var faqLink = new LinkLabel
        {
            Text         = "📚 Browse the FAQ",
            Font         = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            AutoSize     = true,
            LinkColor    = GetHelpForm.BrandBlue,
            ActiveLinkColor = GetHelpForm.ButtonHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin       = new Padding(0, 4, 0, 0)
        };
        faqLink.Click += (_, _) => GetHelpForm.OpenUrl(GetHelpForm.FaqLink);
        faqRow.Controls.Add(faqPrefix);
        faqRow.Controls.Add(faqLink);
        body.Controls.Add(faqRow);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var sendBtn = MakePrimaryButton("✉  Send to helpdesk now");
        sendBtn.Click += (_, _) =>
        {
            Host.Report.AffectedScope = SelectedRadio(scope);
            Host.Report.RecentChanges = changes.Text;
            Host.Report.FreeText      = extra.Text;
            Host.SendHelpdeskEmail();
        };
        var resolvedBtn = MakeSecondaryButton("✓  It's working now");
        resolvedBtn.Click += (_, _) => Host.Navigate(() => new ResolvedPage(Host));

        actions.Controls.Add(resolvedBtn);
        actions.Controls.Add(sendBtn);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}

// ====================================================================
// Sending — transient page shown while we wait on SMTP2GO
// ====================================================================

internal sealed class SendingPage : WizardPage
{
    public override string Title => "Sending…";
    public override bool HidesAttachmentBar => true;

    public SendingPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();

        var heading = new Label
        {
            Text      = "📤  Sending your ticket…",
            Font      = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor = GetHelpForm.BrandBlue,
            AutoSize  = true,
            Margin    = new Padding(0, 32, 0, 8)
        };
        body.Controls.Add(heading);

        body.Controls.Add(MakeIntro(
            "Hang tight — this usually takes a couple of seconds. We're attaching " +
            "everything we collected (your computer name, what's on screen, open apps) " +
            "and sending it to the helpdesk."));

        // ProgressBar in marquee mode is the cheapest "something is happening"
        // indicator we can render — no extra assets and no animation timer.
        var progress = new ProgressBar
        {
            Style    = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Width    = 580,
            Height   = 14,
            Margin   = new Padding(8, 12, 0, 0)
        };
        body.Controls.Add(progress);

        Controls.Add(body);
    }
}

// ====================================================================
// Sent confirmation — terminal page after auto-send
// ====================================================================

internal sealed class SentPage : WizardPage
{
    public override string Title { get; }
    public override bool HidesAttachmentBar => true;

    public SentPage(GetHelpForm host,
                    EmailSender.SendResult sendResult,
                    string subject,
                    string body) : base(host)
    {
        bool autoSent = sendResult.Status == EmailSender.SendStatus.Sent;
        Title = autoSent ? "Sent!" : "Couldn't send";

        var bodyPanel = MakeBody();

        var hooray = new Label
        {
            Text      = autoSent
                        ? "🎉  Sent to the helpdesk!"
                        : "⚠  Couldn't send your ticket",
            Font      = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor = autoSent ? GetHelpForm.BrandBlue : Color.FromArgb(180, 60, 30),
            AutoSize  = true,
            Margin    = new Padding(0, 16, 0, 8)
        };
        bodyPanel.Controls.Add(hooray);

        string explainer = autoSent
            ? "Your ticket is on its way to the helpdesk. They'll reply to your " +
              "email — keep an eye on your inbox. You can close this window."
            : "We couldn't reach the helpdesk's mail service to send your ticket. Copy " +
              "the message below and email it to " + GetHelpForm.HelpdeskTo +
              " from any device.";

        bodyPanel.Controls.Add(MakeIntro(explainer));

        // For the failure case, show the rendered ticket in a read-only
        // multiline box so the user can copy it manually.
        if (!autoSent)
        {
            var copyLabel = MakeQuestion("Your ticket (copy this if needed):");
            bodyPanel.Controls.Add(copyLabel);

            var subjectBox = new TextBox
            {
                Text       = subject,
                ReadOnly   = true,
                Width      = 600,
                Font       = new Font("Consolas", 9.5F),
                Margin     = new Padding(8, 0, 0, 6),
                BackColor  = GetHelpForm.SoftGrey
            };
            bodyPanel.Controls.Add(subjectBox);

            var bodyBox = new TextBox
            {
                Text       = body,
                ReadOnly   = true,
                Multiline  = true,
                ScrollBars = ScrollBars.Vertical,
                Width      = 600,
                Height     = 220,
                Font       = new Font("Consolas", 9F),
                Margin     = new Padding(8, 0, 0, 8),
                BackColor  = GetHelpForm.SoftGrey
            };
            bodyPanel.Controls.Add(bodyBox);

            if (!string.IsNullOrEmpty(sendResult.ErrorDetail))
            {
                var diag = new Label
                {
                    Text      = "Diagnostics: " + sendResult.ErrorDetail,
                    Font      = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                    ForeColor = GetHelpForm.TextDim,
                    AutoSize  = false,
                    Width     = 600,
                    Height    = 32,
                    Margin    = new Padding(8, 0, 0, 8)
                };
                bodyPanel.Controls.Add(diag);
            }
        }

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };
        var doneBtn = MakePrimaryButton("All done");
        doneBtn.Click += (_, _) => Host.Close();

        var faqBtn = MakeSecondaryButton("📚  Browse the FAQ");
        faqBtn.Click += (_, _) =>
        {
            GetHelpForm.OpenUrl(GetHelpForm.FaqLink);
            Host.Close();
        };

        actions.Controls.Add(doneBtn);
        actions.Controls.Add(faqBtn);
        bodyPanel.Controls.Add(actions);
        Controls.Add(bodyPanel);
    }
}

// ====================================================================
// Resolved (self-fix) — terminal page if the user says it's working
// ====================================================================

internal sealed class ResolvedPage : WizardPage
{
    public override string Title => "Glad it's working!";
    public override bool HidesAttachmentBar => true;

    public ResolvedPage(GetHelpForm host) : base(host)
    {
        var body = MakeBody();

        var hooray = new Label
        {
            Text      = "🎉  Glad it's working!",
            Font      = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor = GetHelpForm.BrandBlue,
            AutoSize  = true,
            Margin    = new Padding(0, 16, 0, 8)
        };
        body.Controls.Add(hooray);

        body.Controls.Add(MakeIntro(
            "Nice work — that saves the helpdesk a real amount of time. " +
            "If you'd like, drop a quick note about what worked so we can " +
            "share it in the FAQ. We'll auto-send it for you."));

        var note = MakeTextBox(80, multiline: true);
        body.Controls.Add(note);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 16, 0, 0),
            WrapContents  = false
        };

        var doneBtn = MakePrimaryButton("All done");
        doneBtn.Click += (_, _) => Host.Close();

        var faqBtn = MakeSecondaryButton("📚  Browse the FAQ");
        faqBtn.Click += (_, _) =>
        {
            GetHelpForm.OpenUrl(GetHelpForm.FaqLink);
            Host.Close();
        };

        var sendAnyway = MakeSecondaryButton("Send note to helpdesk");
        sendAnyway.Click += (_, _) =>
        {
            Host.Report.FreeText = "(Resolved by self) " + note.Text;
            Host.SendHelpdeskEmail();
        };

        actions.Controls.Add(doneBtn);
        actions.Controls.Add(faqBtn);
        actions.Controls.Add(sendAnyway);
        body.Controls.Add(actions);
        Controls.Add(body);
    }
}
