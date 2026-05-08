using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace HelpdeskHeroes;

/// <summary>
/// On-demand snapshot of "what is the user actually looking at right now?"
/// Captures the foreground window (process / title / state) plus any visible
/// modal dialog text from any process — the kind of "Word can't open the file"
/// or "Your password has expired" pop-ups that users usually describe
/// imprecisely in tickets.
///
/// Same defensive style as BrowserTabs / OpenApps: every call swallows its
/// own exceptions, the whole pass is bounded by a hard timeout, and the worst
/// case is an empty <see cref="Snapshot"/> — never a crash.
///
/// Called by <see cref="TroubleshootingReport"/> at email build time and by
/// the wrap-up page's "Look at my screen" button so the captured screen
/// reflects what's visible at send time, not when the wizard was opened.
/// </summary>
internal static class ActiveContext
{
    /// <summary>One captured visible dialog window from any process.</summary>
    internal sealed record DialogText(string AppName, string Title, string Body);

    /// <summary>Result of a single <see cref="Capture"/> call.</summary>
    internal sealed record Snapshot(
        string ForegroundApp,        // friendly name, e.g. "Word"
        string ForegroundTitle,      // the window's titlebar
        string ForegroundState,      // "maximized" / "normal" / "minimized" / "(unknown)"
        IReadOnlyList<DialogText> Dialogs)
    {
        public static Snapshot Empty { get; } =
            new("(unknown)", "", "(unknown)", Array.Empty<DialogText>());

        public bool HasAnything =>
            !string.IsNullOrWhiteSpace(ForegroundTitle) || Dialogs.Count > 0;
    }

    /// <summary>Hard ceiling for the whole pass.</summary>
    private const int CollectTimeoutMs = 2500;

    /// <summary>How deep to recurse into a dialog's tree when reading body text.</summary>
    private const int MaxDialogTextSnippetLength = 600;

    /// <summary>Cap on dialogs we'll capture per pass — avoids runaway tickets.</summary>
    private const int MaxDialogs = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Take a single snapshot of the screen via UI Automation. Always returns
    /// a non-null Snapshot; check <see cref="Snapshot.HasAnything"/> to see if
    /// it's worth rendering.
    /// </summary>
    public static Snapshot Capture()
    {
        int deadline = Environment.TickCount + CollectTimeoutMs;

        string fgApp   = "(unknown)";
        string fgTitle = "";
        string fgState = "(unknown)";

        try
        {
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
            {
                (fgApp, fgTitle, fgState) = ReadForeground(fgHwnd);
            }
        }
        catch { /* fall through with defaults */ }

        var dialogs = new List<DialogText>();
        try
        {
            CollectVisibleDialogs(dialogs, deadline);
        }
        catch { /* leave dialogs as-is */ }

        return new Snapshot(fgApp, fgTitle, fgState, dialogs);
    }

    private static (string app, string title, string state) ReadForeground(IntPtr hwnd)
    {
        string app   = "(unknown)";
        string title = "";
        string state = "(unknown)";

        try
        {
            GetWindowThreadProcessId(hwnd, out uint pidU);
            int pid = (int)pidU;
            if (pid > 0)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    app = FriendlyAppName(p);
                }
                catch { /* keep default */ }
            }
        }
        catch { }

        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element != null)
            {
                title = SafeName(element);

                if (element.TryGetCurrentPattern(WindowPattern.Pattern, out object wpObj)
                    && wpObj is WindowPattern wp)
                {
                    state = wp.Current.WindowVisualState switch
                    {
                        WindowVisualState.Maximized => "maximized",
                        WindowVisualState.Minimized => "minimized",
                        WindowVisualState.Normal    => "normal",
                        _                           => "(unknown)"
                    };
                }
            }
        }
        catch { }

        return (app, title, state);
    }

    /// <summary>
    /// Walk the top-level window list and capture any visible modal dialogs.
    /// "Modal dialog" by Win32 is fuzzy; we use WindowPattern.IsModal which
    /// is what most app frameworks set correctly.
    /// </summary>
    private static void CollectVisibleDialogs(List<DialogText> sink, int deadlineTicks)
    {
        AutomationElementCollection topLevel;
        try
        {
            topLevel = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.Window));
        }
        catch
        {
            return;
        }

        foreach (AutomationElement window in topLevel)
        {
            if (sink.Count >= MaxDialogs) break;
            if (TimedOut(deadlineTicks)) break;

            try
            {
                if (!window.TryGetCurrentPattern(WindowPattern.Pattern, out object wpObj)
                    || wpObj is not WindowPattern wp)
                {
                    continue;
                }

                bool isModal = false;
                try { isModal = wp.Current.IsModal; } catch { }
                if (!isModal) continue;

                if (wp.Current.WindowVisualState == WindowVisualState.Minimized) continue;

                string title = SafeName(window);
                if (string.IsNullOrWhiteSpace(title)) continue;

                string app = "(unknown)";
                try
                {
                    using var p = Process.GetProcessById(window.Current.ProcessId);
                    app = FriendlyAppName(p);
                }
                catch { }

                string body = ReadDialogBody(window, deadlineTicks);
                if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                sink.Add(new DialogText(app, title, body));
            }
            catch
            {
                // Skip this window, keep scanning others.
            }
        }
    }

    /// <summary>
    /// Read the static text inside a dialog. Walks Text/Edit descendants and
    /// concatenates their Name with single-space separators, capped to a
    /// reasonable size so we don't dump an entire embedded web page into the
    /// ticket.
    /// </summary>
    private static string ReadDialogBody(AutomationElement window, int deadlineTicks)
    {
        var sb = new StringBuilder();
        try
        {
            var textCondition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            var nodes = window.FindAll(TreeScope.Descendants, textCondition);

            // Skip the title-bar text (already captured as the dialog Title)
            // and dedupe near-identical labels.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutomationElement node in nodes)
            {
                if (TimedOut(deadlineTicks)) break;

                string txt = SafeName(node);
                if (string.IsNullOrWhiteSpace(txt)) continue;
                if (txt.Length < 2) continue;
                if (!seen.Add(txt)) continue;

                if (sb.Length > 0) sb.Append("  ");
                sb.Append(txt);

                if (sb.Length >= MaxDialogTextSnippetLength)
                {
                    sb.Length = MaxDialogTextSnippetLength;
                    sb.Append('…');
                    break;
                }
            }
        }
        catch
        {
            // Best effort.
        }
        return sb.ToString().Trim();
    }

    private static string FriendlyAppName(Process p)
    {
        try
        {
            string name = p.ProcessName;
            // Reuse OpenApps' curated mapping for the common names so we
            // present "Word" instead of "WINWORD".
            if (OpenApps.TryGetFriendlyName(name, out string? friendly) && friendly != null)
            {
                return friendly;
            }
            try
            {
                var fvi = p.MainModule?.FileVersionInfo;
                if (fvi != null)
                {
                    if (!string.IsNullOrWhiteSpace(fvi.FileDescription)) return fvi.FileDescription!;
                    if (!string.IsNullOrWhiteSpace(fvi.ProductName))     return fvi.ProductName!;
                }
            }
            catch { }
            return string.IsNullOrEmpty(name) ? "(unknown)" : name;
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Current.Name ?? ""; }
        catch { return ""; }
    }

    private static bool TimedOut(int deadlineTicks)
    {
        return unchecked(Environment.TickCount - deadlineTicks) > 0;
    }
}
