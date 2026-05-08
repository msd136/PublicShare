using System;
using System.Collections.Generic;
using System.Text;

namespace HelpdeskHeroes;

/// <summary>
/// Captures every answer the user gives in the Get Help wizard
/// and renders the final helpdesk email subject + body.
/// All auto-collected sections (system info, foreground window + dialogs,
/// open apps, browser tabs) are appended at build time so the helpdesk
/// gets a full picture without the user typing anything.
/// </summary>
internal sealed class TroubleshootingReport
{
    public string Category { get; set; } = "Something else";

    /// <summary>True if the user marked the issue as actively blocking their work.</summary>
    public bool IsUrgent { get; set; }

    /// <summary>"Just me", "A few of us", "Everyone in my area / team", or "" if unanswered.</summary>
    public string AffectedScope { get; set; } = "";

    /// <summary>Free-text "anything change today?" answer.</summary>
    public string RecentChanges { get; set; } = "";

    /// <summary>
    /// Per-category structured answers. Key = question label, value = user's answer.
    /// Order is preserved so the email reads naturally.
    /// </summary>
    public List<KeyValuePair<string, string>> Answers { get; } = new();

    /// <summary>
    /// Self-service fixes the user said they've already tried.
    /// </summary>
    public List<string> AlreadyTried { get; } = new();

    /// <summary>
    /// Anything additional the user typed at the end.
    /// </summary>
    public string FreeText { get; set; } = "";

    /// <summary>
    /// Files the user has staged (file picker or clipboard paste).
    /// Bytes are not held in memory until the email is built — see
    /// <see cref="AttachmentSet"/>.
    /// </summary>
    public AttachmentSet Attachments { get; } = new();

    // ---- Cached collection snapshots ----
    // BuildBody and BuildHtmlBody used to each call OpenApps.Collect(),
    // BrowserTabs.Collect(), and ActiveContext.Capture() — each of those
    // does a UIA enumeration that takes 1–4 seconds, so building both
    // bodies meant the user waited twice as long. We cache the first
    // result on the report and let the second build reuse it. Cache
    // lifetime is one report instance: a new wizard run = a fresh report
    // = fresh collection.
    private List<OpenApps.App>?              _cachedApps;
    private List<BrowserTabs.BrowserWindow>? _cachedTabs;
    private bool _activeContextCollected;

    /// <summary>
    /// Latest ActiveContext snapshot (foreground window + visible dialogs).
    /// Captured automatically at email build time; refreshed by the wrap-up
    /// page's "Look at my screen" button.
    /// </summary>
    internal ActiveContext.Snapshot? ScreenSnapshot { get; set; }

    /// <summary>
    /// True if the user used the "Look at my screen" button. Drives a
    /// "(refreshed at send time)" annotation in the email so the helpdesk
    /// knows the snapshot reflects what was actually visible when they clicked
    /// send, not when they opened the wizard.
    /// </summary>
    internal bool ScreenSnapshotRefreshedManually { get; set; }

    public void AddAnswer(string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return;
        Answers.Add(new KeyValuePair<string, string>(question, answer.Trim()));
    }

    /// <summary>
    /// Collect-once accessor for the open-apps list. The first call runs the
    /// UIA pass; subsequent calls reuse the cached result. Returns an empty
    /// list (never null) on collection failure so callers can iterate freely.
    /// </summary>
    private List<OpenApps.App> GetApps()
    {
        if (_cachedApps != null) return _cachedApps;
        try { _cachedApps = OpenApps.Collect() ?? new List<OpenApps.App>(); }
        catch { _cachedApps = new List<OpenApps.App>(); }
        return _cachedApps;
    }

    /// <summary>Collect-once accessor for browser windows + tabs.</summary>
    private List<BrowserTabs.BrowserWindow> GetTabs()
    {
        if (_cachedTabs != null) return _cachedTabs;
        try { _cachedTabs = BrowserTabs.Collect() ?? new List<BrowserTabs.BrowserWindow>(); }
        catch { _cachedTabs = new List<BrowserTabs.BrowserWindow>(); }
        return _cachedTabs;
    }

    /// <summary>
    /// Collect-once accessor for the on-screen snapshot. Different signature
    /// from the two above because <see cref="ScreenSnapshot"/> may already
    /// have been set by RefreshScreenSnapshot — we honour that and don't
    /// re-capture.
    /// </summary>
    private ActiveContext.Snapshot? GetActiveContext()
    {
        if (ScreenSnapshot != null) return ScreenSnapshot;
        if (_activeContextCollected) return ScreenSnapshot;
        _activeContextCollected = true;
        try { ScreenSnapshot = ActiveContext.Capture(); }
        catch { ScreenSnapshot = null; }
        return ScreenSnapshot;
    }

    public string BuildSubject()
    {
        string prefix = IsUrgent ? "[URGENT] " : "";
        // Tag with computer name so the helpdesk can route by lab / cart at a glance.
        return $"{prefix}Helpdesk Request — {Category} — {SystemInfo.ComputerName}";
    }

    public string BuildBody()
    {
        var sb = new StringBuilder();
        sb.Append("Hi Helpdesk,\r\n\r\n");

        if (IsUrgent)
        {
            sb.Append("⚠ Blocking my work right now: YES\r\n\r\n");
        }

        // Identify the user up top — auto-filled, no typing required.
        // We include the resolved email separately from the SAM account name
        // because helpdesk staff often want to copy/paste it as a Reply-To
        // when handling the ticket from a non-Outlook system (e.g. Jira).
        sb.Append($"From: {SystemInfo.UserName} on {SystemInfo.ComputerName}\r\n");
        try
        {
            string resolved = UserIdentity.ResolveEmail();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                sb.Append($"Email: {resolved}\r\n");
            }
        }
        catch { /* never block ticket build on identity lookup */ }
        sb.Append($"Category: {Category}\r\n");
        if (!string.IsNullOrWhiteSpace(AffectedScope))
        {
            sb.Append($"Affected: {AffectedScope}\r\n");
        }
        if (!string.IsNullOrWhiteSpace(RecentChanges))
        {
            sb.Append($"Recent changes: {RecentChanges.Trim()}\r\n");
        }
        sb.Append("\r\n");

        if (Answers.Count > 0)
        {
            sb.Append("--- Details ---\r\n");
            foreach (var kvp in Answers)
            {
                sb.Append(kvp.Key);
                sb.Append(": ");
                sb.Append(kvp.Value);
                sb.Append("\r\n");
            }
            sb.Append("\r\n");
        }

        if (AlreadyTried.Count > 0)
        {
            sb.Append("--- Already tried ---\r\n");
            foreach (var step in AlreadyTried)
            {
                sb.Append("• ");
                sb.Append(step);
                sb.Append("\r\n");
            }
            sb.Append("\r\n");
        }

        if (!string.IsNullOrWhiteSpace(FreeText))
        {
            sb.Append("--- Anything else ---\r\n");
            sb.Append(FreeText.Trim());
            sb.Append("\r\n\r\n");
        }

        if (Attachments.Count > 0)
        {
            sb.Append("--- Attachments ---\r\n");
            foreach (var a in Attachments.Items)
            {
                sb.Append("• ");
                sb.Append(a.DisplayName);
                sb.Append("  (");
                sb.Append(AttachmentSet.FormatSize(a.SizeBytes));
                if (a.IsClipboardCapture) sb.Append(", pasted from clipboard");
                sb.Append(")\r\n");
            }
            sb.Append("\r\n");
        }

        // System fingerprint (computer name, serial, Wi-Fi, IP, OS, version, time).
        sb.Append(SystemInfo.GatherFormatted());
        sb.Append("\r\n");

        // Live snapshots from UI Automation — what's on screen, what's open,
        // what tabs are loaded. Each section silently no-ops if it picked up
        // nothing useful.
        AppendActiveContext(sb);
        AppendOpenApps(sb);
        AppendBrowserTabs(sb);

        sb.Append("— Sent automatically by Helpdesk Heroes\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Render the same data as <see cref="BuildBody"/> but as a styled HTML
    /// document. Used as the SMTP2GO <c>html_body</c>. The plain-text version
    /// is still produced for the SentPage failure-mode "copy this manually"
    /// box — pasting raw HTML source into a Gmail compose window would be
    /// unreadable.
    ///
    /// All dynamic strings flow through <see cref="HtmlReportBuilder.Esc"/>
    /// to neutralize stray markup in user-typed text or copied error
    /// messages. Layout uses tables exclusively because Outlook on Windows
    /// renders HTML through Word's 2007-era engine and treats divs/flex/grid
    /// inconsistently.
    /// </summary>
    public string BuildHtmlBody()
    {
        var h = new HtmlReportBuilder();
        h.OpenDocument();

        h.Header("Hi Helpdesk,");

        if (IsUrgent) h.UrgentBanner();

        // ---- Who / what ----
        var top = new List<(string, string)>
        {
            ("From",     $"{SystemInfo.UserName} on {SystemInfo.ComputerName}"),
        };
        try
        {
            string resolved = UserIdentity.ResolveEmail();
            if (!string.IsNullOrWhiteSpace(resolved))
                top.Add(("Email", resolved));
        }
        catch { /* never block ticket build on identity lookup */ }
        top.Add(("Category", Category));
        if (!string.IsNullOrWhiteSpace(AffectedScope))
            top.Add(("Affected", AffectedScope));
        if (!string.IsNullOrWhiteSpace(RecentChanges))
            top.Add(("Recent changes", RecentChanges.Trim()));
        h.KvpTable(top);

        // ---- Wizard answers ----
        if (Answers.Count > 0)
        {
            h.SectionHeading("Details");
            var rows = new List<(string, string)>(Answers.Count);
            foreach (var kvp in Answers) rows.Add((kvp.Key, kvp.Value));
            h.KvpTable(rows);
        }

        // ---- Already tried ----
        if (AlreadyTried.Count > 0)
        {
            h.SectionHeading("Already tried");
            h.BulletList(AlreadyTried);
        }

        // ---- Free-form prose ----
        if (!string.IsNullOrWhiteSpace(FreeText))
        {
            h.SectionHeading("Anything else");
            h.Paragraph(FreeText.Trim());
        }

        // ---- Attachments manifest ----
        // Even though SMTP2GO carries the actual files in the email, listing
        // them in the body matters: the helpdesk's ticketing system might
        // strip attachments (some do, for AV scanning) and the manifest is
        // the only signal of what was meant to be attached.
        if (Attachments.Count > 0)
        {
            h.SectionHeading("Attachments");
            var rows = new List<(string, string)>(Attachments.Count);
            foreach (var a in Attachments.Items)
            {
                string label = AttachmentSet.FormatSize(a.SizeBytes);
                if (a.IsClipboardCapture) label += " · pasted from clipboard";
                rows.Add((a.DisplayName, label));
            }
            h.KvpTable(rows);
        }

        // ---- On-screen snapshot (highlighted callout) ----
        BuildHtmlActiveContext(h);

        // ---- Auto-collected fingerprint ----
        h.SectionHeading("Auto-collected system info");
        h.KvpTable(SystemInfo.GatherKvp());

        // ---- Open apps ----
        BuildHtmlOpenApps(h);

        // ---- Browser tabs ----
        BuildHtmlBrowserTabs(h);

        // ---- Footer ----
        h.Footer($"— Sent automatically by Helpdesk Heroes v{SystemInfo.AppVersion}");

        h.CloseDocument();
        return h.ToString();
    }

    /// <summary>
    /// Render the on-screen snapshot inside the highlighted callout box.
    /// This section is intentionally visually distinct because it's the
    /// piece of info the helpdesk uses most — "what was actually happening
    /// when the user hit send."
    /// </summary>
    private void BuildHtmlActiveContext(HtmlReportBuilder h)
    {
        var snap = GetActiveContext();
        if (snap == null || !snap.HasAnything) return;

        string title = ScreenSnapshotRefreshedManually
            ? "On screen at send time"
            : "On screen when ticket was started";
        h.OpenCallout(title);

        if (!string.IsNullOrWhiteSpace(snap.ForegroundTitle)
            || !string.Equals(snap.ForegroundApp, "(unknown)", StringComparison.Ordinal))
        {
            string focused = snap.ForegroundApp;
            if (!string.IsNullOrWhiteSpace(snap.ForegroundTitle))
                focused += $" — \"{snap.ForegroundTitle}\"";
            if (!string.Equals(snap.ForegroundState, "(unknown)", StringComparison.Ordinal))
                focused += $" ({snap.ForegroundState})";

            h.InlineKvpTable(new[] { ("Focused", focused) });
        }

        foreach (var d in snap.Dialogs)
        {
            h.InlineDialog(d.AppName, d.Title, d.Body);
        }

        h.CloseCallout();
    }

    /// <summary>HTML version of the open-apps roll-up.</summary>
    private void BuildHtmlOpenApps(HtmlReportBuilder h)
    {
        var apps = GetApps();
        if (apps.Count == 0) return;

        h.SectionHeading("Open apps");
        var lines = new List<string>(apps.Count);
        foreach (var a in apps)
        {
            string line = a.DisplayName;
            if (!string.Equals(a.DisplayName, a.ProcessName, StringComparison.OrdinalIgnoreCase))
                line += $" ({a.ProcessName}.exe)";
            if (!a.IsResponding) line += "  ⚠ NOT RESPONDING";
            lines.Add(line);
        }
        h.BulletList(lines);
    }

    /// <summary>HTML version of the browser-tabs roll-up.</summary>
    private void BuildHtmlBrowserTabs(HtmlReportBuilder h)
    {
        var windows = GetTabs();
        if (windows.Count == 0) return;

        h.SectionHeading("Browser tabs");
        // Flatten window → tabs into one annotated list. We lose the
        // per-window grouping the plain-text version uses, but in HTML the
        // alternative is a nested table that Outlook tends to mangle.
        // The "[Browser - Window]" prefix on each line keeps the grouping
        // visible without needing nested layout.
        var lines = new List<string>();
        foreach (var win in windows)
        {
            // Optional window header shown as a non-bulleted line. We do this
            // by emitting one entry that's "Browser — Window [tags]" first,
            // then bullet entries under it. To stay inside the BulletList API
            // (single flat list), we'll prefix tab lines with the browser name
            // and only add one window-summary entry when there are no tabs.
            var tags = new List<string>(2);
            if (!string.IsNullOrEmpty(win.DisplayState) && win.DisplayState != "background")
                tags.Add(win.DisplayState);
            if (!string.IsNullOrEmpty(win.Monitor))
                tags.Add(win.Monitor);
            string winSuffix = tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : "";

            if (win.Tabs.Count == 0)
            {
                string header = string.IsNullOrWhiteSpace(win.WindowTitle)
                    ? win.Browser
                    : $"{win.Browser} — {win.WindowTitle}";
                lines.Add($"{header}{winSuffix}  (no tabs detected)");
                continue;
            }

            foreach (var tab in win.Tabs)
            {
                string marker = tab.IsActive ? "● " : "";
                string title  = string.IsNullOrWhiteSpace(tab.Title) ? "(untitled)" : tab.Title;
                string line = $"[{win.Browser}] {marker}{title}";
                if (!string.IsNullOrWhiteSpace(tab.Url))
                    line += $"  —  {tab.Url}";
                lines.Add(line);
            }
        }
        if (lines.Count > 0) h.BulletList(lines);
    }

    /// <summary>
    /// Capture an <see cref="ActiveContext.Snapshot"/>. Called by the wrap-up
    /// page so the email body reflects what was on screen at send time, not
    /// the (possibly stale) state from wizard launch. Safe to call multiple
    /// times.
    /// </summary>
    public ActiveContext.Snapshot RefreshScreenSnapshot(bool manual)
    {
        try
        {
            ScreenSnapshot = ActiveContext.Capture();
        }
        catch
        {
            ScreenSnapshot = ActiveContext.Snapshot.Empty;
        }
        if (manual) ScreenSnapshotRefreshedManually = true;
        return ScreenSnapshot;
    }

    /// <summary>
    /// Auto-collected list of apps the user has open (anything with a
    /// visible top-level window). Silently skipped if nothing is detected.
    /// </summary>
    private void AppendOpenApps(StringBuilder sb)
    {
        var apps = GetApps();
        if (apps.Count == 0) return;

        sb.Append("--- Open apps (auto-collected) ---\r\n");
        foreach (var a in apps)
        {
            sb.Append("• ");
            sb.Append(a.DisplayName);
            // Include the bare process name in parens when it differs from
            // the display name — helps the helpdesk grep logs.
            if (!string.Equals(a.DisplayName, a.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" (");
                sb.Append(a.ProcessName);
                sb.Append(".exe)");
            }
            if (!a.IsResponding)
            {
                sb.Append("  ⚠ NOT RESPONDING");
            }
            sb.Append("\r\n");
        }
        sb.Append("\r\n");
    }

    /// <summary>
    /// Auto-collected list of open Chrome / Edge / Firefox tabs. Best-effort:
    /// if no browser windows are open or UI Automation can't read them, we
    /// silently skip the section so the email stays clean.
    /// </summary>
    private void AppendBrowserTabs(StringBuilder sb)
    {
        var windows = GetTabs();
        if (windows.Count == 0) return;

        sb.Append("--- Open browser tabs (auto-collected) ---\r\n");
        foreach (var win in windows)
        {
            string header = string.IsNullOrWhiteSpace(win.WindowTitle)
                ? win.Browser
                : $"{win.Browser} — {win.WindowTitle}";

            // Annotate window-level state inline so "the issue is on the
            // projector / the minimized window" reads at a glance.
            var tags = new List<string>(2);
            if (!string.IsNullOrEmpty(win.DisplayState) && win.DisplayState != "background")
                tags.Add(win.DisplayState);
            if (!string.IsNullOrEmpty(win.Monitor))
                tags.Add(win.Monitor);
            if (tags.Count > 0)
            {
                header += $"  [{string.Join(", ", tags)}]";
            }

            sb.Append(header);
            sb.Append("\r\n");

            if (win.Tabs.Count == 0)
            {
                sb.Append("  (no tabs detected)\r\n");
                continue;
            }

            foreach (var tab in win.Tabs)
            {
                string marker = tab.IsActive ? "● " : "  ";
                string title  = string.IsNullOrWhiteSpace(tab.Title) ? "(untitled)" : tab.Title;
                sb.Append(marker);
                sb.Append(title);
                if (!string.IsNullOrWhiteSpace(tab.Url))
                {
                    sb.Append("  —  ");
                    sb.Append(tab.Url);
                }
                sb.Append("\r\n");
            }
            sb.Append("\r\n");
        }
    }

    /// <summary>
    /// Render the foreground-window snapshot + any visible modal dialogs the
    /// UIA pass picked up. Auto-captures at email build time if the wrap-up
    /// page didn't already.
    /// </summary>
    private void AppendActiveContext(StringBuilder sb)
    {
        var snap = GetActiveContext();
        if (snap == null || !snap.HasAnything) return;

        string heading = ScreenSnapshotRefreshedManually
            ? "--- On screen at send time (auto-collected) ---\r\n"
            : "--- On screen when ticket was started (auto-collected) ---\r\n";
        sb.Append(heading);

        if (!string.IsNullOrWhiteSpace(snap.ForegroundTitle)
            || !string.Equals(snap.ForegroundApp, "(unknown)", StringComparison.Ordinal))
        {
            sb.Append("Focused: ");
            sb.Append(snap.ForegroundApp);
            if (!string.IsNullOrWhiteSpace(snap.ForegroundTitle))
            {
                sb.Append(" — \"");
                sb.Append(snap.ForegroundTitle);
                sb.Append('"');
            }
            if (!string.Equals(snap.ForegroundState, "(unknown)", StringComparison.Ordinal))
            {
                sb.Append(" (");
                sb.Append(snap.ForegroundState);
                sb.Append(')');
            }
            sb.Append("\r\n");
        }

        foreach (var d in snap.Dialogs)
        {
            sb.Append("Dialog: ");
            sb.Append(d.AppName);
            sb.Append(" — \"");
            sb.Append(d.Title);
            sb.Append('"');
            if (!string.IsNullOrWhiteSpace(d.Body))
            {
                sb.Append("\r\n   ");
                sb.Append(d.Body);
            }
            sb.Append("\r\n");
        }
        sb.Append("\r\n");
    }
}
