using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace HelpdeskHeroes;

/// <summary>
/// Small helper for assembling the HTML email body. Centralizes:
///   - HTML escaping (every dynamic string flows through Esc)
///   - Inline styles (Outlook ignores &lt;style&gt; tags reliably; only inline
///     attributes render across Outlook desktop, OWA, mobile Outlook, and Gmail)
///   - Section wrappers and table rows
///
/// The choice to use tables for layout is deliberate. Outlook on Windows
/// uses Word's HTML rendering engine, which has 2007-era CSS support —
/// flex/grid don't work, margins on divs are inconsistent, but tables do
/// what you tell them. The output won't look like a 2026 web page, but it
/// will look the same in every helpdesk inbox.
///
/// Color palette is anchored on the BrandBlue used in the WinForms UI
/// (#005A9E) so the email feels cohesive with the app.
/// </summary>
internal sealed class HtmlReportBuilder
{
    // Anchor palette — keep these in sync with GetHelpForm.BrandBlue / etc.
    public const string BrandBlue   = "#005A9E";
    public const string BrandLight  = "#E8F1F8";
    public const string TextPrimary = "#222222";
    public const string TextMuted   = "#666666";
    public const string BorderGrey  = "#D9DDE3";
    public const string SoftGrey    = "#F5F7FA";
    public const string UrgentRed   = "#B43C1E";
    public const string UrgentBg    = "#FFF4E5"; // soft amber
    public const string CalloutBg   = "#F0F7FC"; // very light blue
    public const string CalloutBd   = "#B6D7EE";

    // System font stack — first one available wins. Segoe UI is preferred
    // on Windows where most helpdesk clients run; the fallbacks cover
    // recipients on mac/linux/mobile.
    public const string FontStack =
        "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif";

    private readonly StringBuilder _sb = new();

    /// <summary>HTML-escape any string that came from user input or live data.</summary>
    public static string Esc(string? raw)
    {
        return string.IsNullOrEmpty(raw) ? "" : WebUtility.HtmlEncode(raw);
    }

    /// <summary>Append a raw HTML chunk — caller is responsible for escaping.</summary>
    public HtmlReportBuilder Raw(string html) { _sb.Append(html); return this; }

    /// <summary>
    /// Open the document. Wraps in a 600px-max table — wider emails sprawl
    /// awkwardly on widescreen monitors and Outlook clips wide tables anyway.
    /// </summary>
    public HtmlReportBuilder OpenDocument()
    {
        _sb.Append(
            "<html><body style=\"margin:0;padding:0;background:#FFFFFF;\">" +
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" " +
            "width=\"100%\" style=\"background:#FFFFFF;\">" +
            "<tr><td align=\"center\" style=\"padding:16px;\">" +
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" " +
            $"width=\"600\" style=\"max-width:600px;width:100%;font-family:{FontStack};" +
            $"font-size:14px;color:{TextPrimary};line-height:1.5;\">"
        );
        return this;
    }

    public HtmlReportBuilder CloseDocument()
    {
        _sb.Append("</table></td></tr></table></body></html>");
        return this;
    }

    /// <summary>
    /// Header bar with the hero emoji + greeting. Sets a distinct visual
    /// anchor at the top so helpdesk staff can tell at a glance this came
    /// from the app, not from a user typing freehand.
    /// </summary>
    public HtmlReportBuilder Header(string greeting)
    {
        _sb.Append(
            "<tr><td style=\"padding:0 0 12px 0;\">" +
            $"<div style=\"font-size:13px;color:{TextMuted};letter-spacing:.04em;" +
            "text-transform:uppercase;\">Helpdesk Heroes</div>" +
            $"<div style=\"font-size:18px;font-weight:600;color:{BrandBlue};" +
            "padding-top:2px;\">" + Esc(greeting) + "</div>" +
            "</td></tr>"
        );
        return this;
    }

    /// <summary>
    /// Bright urgent banner. Only emitted when the user ticked the
    /// "blocking my work" box — visually unmistakable in an Outlook
    /// preview pane.
    /// </summary>
    public HtmlReportBuilder UrgentBanner()
    {
        _sb.Append(
            "<tr><td style=\"padding:0 0 16px 0;\">" +
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" " +
            $"width=\"100%\" style=\"background:{UrgentBg};border-left:4px solid {UrgentRed};\">" +
            "<tr><td style=\"padding:10px 14px;\">" +
            $"<span style=\"font-weight:600;color:{UrgentRed};\">⚠ URGENT</span> " +
            $"<span style=\"color:{TextPrimary};\">— user says this is blocking their work right now.</span>" +
            "</td></tr></table></td></tr>"
        );
        return this;
    }

    /// <summary>
    /// Section heading — small caps blue header, slim rule below. Used to
    /// separate "Details", "Already tried", system info, etc.
    /// </summary>
    public HtmlReportBuilder SectionHeading(string title)
    {
        _sb.Append(
            "<tr><td style=\"padding:18px 0 6px 0;\">" +
            $"<div style=\"font-size:11px;font-weight:700;color:{BrandBlue};" +
            $"letter-spacing:.08em;text-transform:uppercase;border-bottom:1px solid {BorderGrey};" +
            "padding-bottom:4px;\">" + Esc(title) + "</div></td></tr>"
        );
        return this;
    }

    /// <summary>
    /// Open the standout callout for the on-screen snapshot section. This
    /// is the section helpdesk staff find most useful day-to-day, so we
    /// give it a tinted background and a blue left rule to draw the eye.
    /// </summary>
    public HtmlReportBuilder OpenCallout(string title)
    {
        _sb.Append(
            "<tr><td style=\"padding:18px 0 0 0;\">" +
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" " +
            $"width=\"100%\" style=\"background:{CalloutBg};border:1px solid {CalloutBd};" +
            $"border-left:4px solid {BrandBlue};\">" +
            "<tr><td style=\"padding:10px 14px;\">" +
            $"<div style=\"font-size:11px;font-weight:700;color:{BrandBlue};" +
            "letter-spacing:.08em;text-transform:uppercase;padding-bottom:6px;\">📺 " +
            Esc(title) + "</div>"
        );
        return this;
    }

    public HtmlReportBuilder CloseCallout()
    {
        _sb.Append("</td></tr></table></td></tr>");
        return this;
    }

    /// <summary>
    /// Render a label/value list as a two-column table — the only layout
    /// approach Outlook reliably renders. Used for the "From / Category /
    /// Affected / Recent changes" block and the system fingerprint.
    /// </summary>
    public HtmlReportBuilder KvpTable(System.Collections.Generic.IEnumerable<(string Label, string Value)> rows)
    {
        _sb.Append(
            "<tr><td style=\"padding:4px 0 0 0;\">" +
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" " +
            "style=\"font-size:14px;\">"
        );
        foreach (var (label, value) in rows)
        {
            _sb.Append(
                "<tr>" +
                $"<td valign=\"top\" style=\"padding:3px 12px 3px 0;color:{TextMuted};" +
                "white-space:nowrap;width:130px;\">" + Esc(label) + "</td>" +
                $"<td valign=\"top\" style=\"padding:3px 0;color:{TextPrimary};\">" +
                Esc(value) + "</td>" +
                "</tr>"
            );
        }
        _sb.Append("</table></td></tr>");
        return this;
    }

    /// <summary>
    /// Same KVP table as above, but rendered inside an already-open callout
    /// or banner cell — i.e. without the outer &lt;tr&gt;&lt;td&gt; wrapper. Used
    /// when KvpTable would double-wrap the rows.
    /// </summary>
    public HtmlReportBuilder InlineKvpTable(System.Collections.Generic.IEnumerable<(string Label, string Value)> rows)
    {
        _sb.Append(
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" " +
            "style=\"font-size:14px;\">"
        );
        foreach (var (label, value) in rows)
        {
            _sb.Append(
                "<tr>" +
                $"<td valign=\"top\" style=\"padding:2px 12px 2px 0;color:{TextMuted};" +
                "white-space:nowrap;width:90px;\">" + Esc(label) + "</td>" +
                $"<td valign=\"top\" style=\"padding:2px 0;color:{TextPrimary};\">" +
                Esc(value) + "</td>" +
                "</tr>"
            );
        }
        _sb.Append("</table>");
        return this;
    }

    /// <summary>Bullet list with brand-blue markers. Used for "Already tried" steps.</summary>
    public HtmlReportBuilder BulletList(System.Collections.Generic.IEnumerable<string> items)
    {
        _sb.Append("<tr><td style=\"padding:4px 0 0 0;\">" +
                   "<ul style=\"margin:4px 0 4px 22px;padding:0;\">");
        foreach (var item in items)
        {
            _sb.Append("<li style=\"margin:2px 0;\">" + Esc(item) + "</li>");
        }
        _sb.Append("</ul></td></tr>");
        return this;
    }

    /// <summary>Free-form prose paragraph (preserves line breaks).</summary>
    public HtmlReportBuilder Paragraph(string text)
    {
        // Preserve newlines as <br> so the user's typed text reads
        // the way they wrote it. Escape first, then swap newlines —
        // NEVER the other way around (escaping after would mangle <br>).
        string escaped = Esc(text).Replace("\n", "<br>");
        _sb.Append("<tr><td style=\"padding:4px 0 0 0;\">" + escaped + "</td></tr>");
        return this;
    }

    /// <summary>Single line of text inside an open callout cell.</summary>
    public HtmlReportBuilder InlineLine(string text)
    {
        _sb.Append("<div style=\"padding:2px 0;\">" + Esc(text) + "</div>");
        return this;
    }

    /// <summary>Single dialog line inside the on-screen callout.</summary>
    public HtmlReportBuilder InlineDialog(string appName, string title, string? body)
    {
        _sb.Append(
            "<div style=\"padding:6px 0 0 0;\">" +
            $"<span style=\"color:{TextMuted};\">Dialog: </span>" +
            $"<strong>{Esc(appName)}</strong> — \"{Esc(title)}\""
        );
        if (!string.IsNullOrWhiteSpace(body))
        {
            _sb.Append(
                $"<div style=\"margin:4px 0 0 12px;padding:6px 10px;background:{SoftGrey};" +
                $"border-left:2px solid {BorderGrey};font-family:Consolas,monospace;" +
                "font-size:13px;white-space:pre-wrap;\">" + Esc(body!) + "</div>"
            );
        }
        _sb.Append("</div>");
        return this;
    }

    /// <summary>Closing footer — small, muted, lives under everything.</summary>
    public HtmlReportBuilder Footer(string text)
    {
        _sb.Append(
            "<tr><td style=\"padding:24px 0 0 0;\">" +
            $"<div style=\"font-size:12px;color:{TextMuted};border-top:1px solid {BorderGrey};" +
            "padding-top:10px;\">" + Esc(text) + "</div></td></tr>"
        );
        return this;
    }

    public override string ToString() => _sb.ToString();
}
