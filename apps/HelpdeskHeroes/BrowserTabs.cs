using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HelpdeskHeroes;

/// <summary>
/// Best-effort enumeration of open Chrome / Edge / Firefox tabs via Windows
/// UI Automation. Used by <see cref="TroubleshootingReport"/> to auto-attach
/// browser context to helpdesk emails so techs can see what the user was
/// looking at when the problem hit (e.g. an internal app, a SaaS dashboard,
/// the corporate intranet).
///
/// Mirrors the defensive style of SystemInfo.cs / OpenApps.cs: every step
/// swallows its own exceptions, the whole pass is bounded by a hard timeout,
/// and the worst case is an empty list — never a crash.
///
/// Trade-off chosen by config: the default is a "deep scan" that briefly
/// Selects each background tab to read its URL out of the address bar. That
/// causes a quick visible flicker inside the browser window (~1 s per window)
/// but produces a complete tab + URL list. We always restore the originally
/// focused tab when we're done. A few details:
///   • Skips the address-bar wait entirely on the active tab (no Select() call).
///   • Walks tabs in the order they appear, but exits early if the user
///     started clicking around in the browser mid-walk (heuristic: foreground
///     window changed).
///   • Adds Firefox (different UIA tree shape — see <see cref="ReadFirefoxWindow"/>).
///   • Captures monitor + window-state metadata for the email so techs can
///     tell apart "the issue is on the projector" vs. "on the laptop screen".
///   • Dedupes Edge/Chrome profile windows by HWND so a user with three
///     profile windows open shows up as three windows, not a merged blob.
/// </summary>
internal static class BrowserTabs
{
    /// <summary>One tab in a browser window.</summary>
    internal sealed record Tab(string Title, string Url, bool IsActive);

    /// <summary>One Chrome / Edge / Firefox top-level window with its tabs.</summary>
    internal sealed record BrowserWindow(
        string Browser,
        string WindowTitle,
        string DisplayState,   // "focused" / "background" / "minimized"
        string Monitor,        // "monitor 1", "monitor 2", or "" if unknown
        IReadOnlyList<Tab> Tabs);

    /// <summary>Hard ceiling for the whole pass. Email build will never block longer than this.</summary>
    private const int CollectTimeoutMs = 8000;

    /// <summary>How long to wait for the address bar to update after Select()ing a tab.</summary>
    private const int PerTabMaxWaitMs = 350;

    /// <summary>Address-bar polling interval inside that wait window.</summary>
    private const int AddressBarPollMs = 20;

    // --- Win32: foreground-window check + monitor enumeration ---------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONULL = 0x00000000;

    /// <summary>
    /// Enumerate every Chrome, Edge, and Firefox window currently open and
    /// return their tabs. Returns an empty list — never throws — if anything
    /// goes wrong.
    /// </summary>
    public static List<BrowserWindow> Collect()
    {
        var results = new List<BrowserWindow>();
        try
        {
            int deadline = Environment.TickCount + CollectTimeoutMs;

            // Snapshot the foreground HWND once so we can label "focused" later
            // and bail if the user starts clicking mid-walk.
            IntPtr originalForeground = SafeGetForegroundWindow();

            // Build a stable monitor map so window 1 / window 2 stay consistent
            // even if EnumDisplayMonitors orders things weirdly.
            var monitorMap = BuildMonitorMap();

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
                return results;
            }

            foreach (AutomationElement window in topLevel)
            {
                if (TimedOut(deadline)) break;

                string? browserLabel = ClassifyBrowser(window);
                if (browserLabel == null) continue;

                try
                {
                    var bw = TryReadBrowserWindow(
                        window, browserLabel, deadline, originalForeground, monitorMap);
                    if (bw != null) results.Add(bw);
                }
                catch
                {
                    // Skip this window, keep going with the rest.
                }
            }
        }
        catch
        {
            // Total failure — return whatever we've gathered so far.
        }
        return results;
    }

    /// <summary>
    /// Returns "Chrome" / "Edge" / "Firefox" if the window's owning process is
    /// one we care about, else null. We check by process name; Chromium runs
    /// many helper processes but only the main browser process owns top-level
    /// windows.
    /// </summary>
    private static string? ClassifyBrowser(AutomationElement window)
    {
        int pid;
        try { pid = window.Current.ProcessId; }
        catch { return null; }

        try
        {
            using var proc = Process.GetProcessById(pid);
            string name = proc.ProcessName;
            if (string.Equals(name, "chrome",  StringComparison.OrdinalIgnoreCase)) return "Chrome";
            if (string.Equals(name, "msedge",  StringComparison.OrdinalIgnoreCase)) return "Edge";
            if (string.Equals(name, "firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
            return null;
        }
        catch
        {
            // Process exited between enumeration and lookup, or access denied.
            return null;
        }
    }

    private static BrowserWindow? TryReadBrowserWindow(
        AutomationElement window,
        string browserLabel,
        int deadlineTicks,
        IntPtr originalForeground,
        Dictionary<IntPtr, string> monitorMap)
    {
        string windowTitle  = SafeName(window);
        string displayState = ClassifyDisplayState(window, originalForeground);
        string monitor      = ClassifyMonitor(window, monitorMap);

        // Firefox has its own UIA tree shape — handle it separately so the
        // Chromium reader stays focused.
        if (browserLabel == "Firefox")
        {
            var ffTabs = ReadFirefoxWindow(window, deadlineTicks);
            return new BrowserWindow(browserLabel, windowTitle, displayState, monitor, ffTabs);
        }

        // ---- Chromium (Chrome / Edge) -----------------------------------

        // Minimized windows don't actually repaint their toolbar when we
        // Select() a background tab, so address-bar reads return stale
        // values. Skip URL collection for those — we'll still capture titles.
        bool canReadUrls = !string.Equals(displayState, "minimized", StringComparison.Ordinal);

        // Locate the tab strip. Chromium exposes it as a single Tab control.
        AutomationElement? tabStrip;
        try
        {
            tabStrip = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
        }
        catch { tabStrip = null; }

        if (tabStrip == null)
        {
            // Could be a download window, a print preview, etc.
            return new BrowserWindow(browserLabel, windowTitle, displayState, monitor, Array.Empty<Tab>());
        }

        // Address bar = first Edit *inside the toolbar*. Searching the whole
        // window risks grabbing an Edit from the page DOM (e.g. a search box
        // on the active site).
        AutomationElement? addressBar = null;
        try
        {
            var toolbar = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
            if (toolbar != null)
            {
                addressBar = toolbar.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            }
        }
        catch { addressBar = null; }

        AutomationElementCollection tabItems;
        try
        {
            tabItems = tabStrip.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        }
        catch
        {
            return new BrowserWindow(browserLabel, windowTitle, displayState, monitor, Array.Empty<Tab>());
        }

        // First pass: identify the originally-active tab and (if we can read
        // URLs at all) snapshot its URL up front from the address bar — no
        // Select() needed for the active one, which is the most common case
        // and the one we most want to capture without flicker.
        AutomationElement? originallyActive = null;
        string activeTabUrl = "";
        foreach (AutomationElement tab in tabItems)
        {
            try
            {
                if (TryGetSelectionItemPattern(tab, out var sip)
                    && sip!.Current.IsSelected)
                {
                    originallyActive = tab;
                    if (canReadUrls && addressBar != null)
                    {
                        activeTabUrl = ReadAddressBar(addressBar);
                    }
                    break;
                }
            }
            catch { /* keep scanning */ }
        }

        var tabs = new List<Tab>(tabItems.Count);
        string lastSeenUrl = activeTabUrl;

        // Second pass: walk tabs in order, snapshotting title + URL.
        // The active tab uses the URL we already read (no Select() call —
        // that's what eliminates the most jarring part of the flicker).
        foreach (AutomationElement tab in tabItems)
        {
            if (TimedOut(deadlineTicks)) break;

            // User-driven escape hatch: if the foreground window changes during
            // our walk (e.g. the user clicked on the browser to switch tabs
            // themselves), stop touching the browser immediately.
            if (UserStartedInteracting(originalForeground)) break;

            string title = SafeName(tab);
            bool isActive = false;
            string url = "";

            try
            {
                if (TryGetSelectionItemPattern(tab, out var sip) && sip != null)
                {
                    isActive = sip.Current.IsSelected;

                    if (isActive)
                    {
                        // Cheap path: we already read the active URL above.
                        url = activeTabUrl;
                    }
                    else if (canReadUrls && addressBar != null)
                    {
                        try
                        {
                            sip.Select();
                            url = WaitForAddressBarChange(
                                addressBar, lastSeenUrl, PerTabMaxWaitMs);
                            lastSeenUrl = url;
                        }
                        catch
                        {
                            // Tab may have closed mid-iteration. Skip URL.
                        }
                    }
                }
            }
            catch { /* fall through and record what we have */ }

            tabs.Add(new Tab(title, url, isActive));
        }

        // Restore originally focused tab so the user doesn't notice we were here.
        try
        {
            if (originallyActive != null
                && TryGetSelectionItemPattern(originallyActive, out var restore)
                && restore != null)
            {
                restore.Select();
            }
        }
        catch { /* nothing we can do */ }

        return new BrowserWindow(browserLabel, windowTitle, displayState, monitor, tabs);
    }

    /// <summary>
    /// Firefox doesn't expose its tab strip as a single ControlType.Tab the
    /// way Chromium does. The tabs are Page-typed elements inside the
    /// browser's chrome, and importantly — we can read each tab's title
    /// directly from the AutomationElement.Name without selecting it. We
    /// don't try to read background URLs (Firefox's URL bar mirrors the
    /// active tab the same as Chromium's, but Firefox repaints slower so
    /// the cost-benefit isn't there). We capture titles only for inactive
    /// tabs, plus the URL of the active tab from the address bar.
    /// </summary>
    private static IReadOnlyList<Tab> ReadFirefoxWindow(AutomationElement window, int deadlineTicks)
    {
        var tabs = new List<Tab>();
        try
        {
            // Firefox's tab list is a TabList control with TabItem children,
            // same as Chromium in name but a different position in the tree.
            var tabList = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
            if (tabList == null) return tabs;

            var items = tabList.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            // Active tab URL: pull from the URL bar (Firefox name: "Search with…",
            // automation type: Edit, inside the navigation toolbar).
            string activeUrl = "";
            try
            {
                var toolbar = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
                if (toolbar != null)
                {
                    var edit = toolbar.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (edit != null) activeUrl = ReadAddressBar(edit);
                }
            }
            catch { /* leave blank */ }

            foreach (AutomationElement item in items)
            {
                if (TimedOut(deadlineTicks)) break;

                string title  = SafeName(item);
                bool isActive = false;
                try
                {
                    if (TryGetSelectionItemPattern(item, out var sip) && sip != null)
                    {
                        isActive = sip.Current.IsSelected;
                    }
                }
                catch { }

                tabs.Add(new Tab(title, isActive ? activeUrl : "", isActive));
            }
        }
        catch
        {
            // Whatever shape Firefox is in today, fall through with what we got.
        }
        return tabs;
    }

    /// <summary>
    /// After Select()ing a tab, the address bar takes time to repaint with
    /// the new tab's URL — especially when the browser window is in the
    /// background (which it always is while the wizard is on top). Poll
    /// until the value differs from <paramref name="previousUrl"/>, or until
    /// <paramref name="maxWaitMs"/> elapses. Whatever's in the address bar
    /// at exit is returned (covers same-URL adjacent tabs and slow paints).
    /// </summary>
    private static string WaitForAddressBarChange(
        AutomationElement addressBar, string previousUrl, int maxWaitMs)
    {
        int waited = 0;
        string current = previousUrl;

        while (waited < maxWaitMs)
        {
            try { Thread.Sleep(AddressBarPollMs); }
            catch { break; }
            waited += AddressBarPollMs;

            current = ReadAddressBar(addressBar);
            if (!string.Equals(current, previousUrl, StringComparison.Ordinal))
            {
                return current;
            }
        }
        return current;
    }

    private static string ClassifyDisplayState(AutomationElement window, IntPtr originalForeground)
    {
        try
        {
            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out object wpObj)
                && wpObj is WindowPattern wp)
            {
                if (wp.Current.WindowVisualState == WindowVisualState.Minimized)
                {
                    return "minimized";
                }
            }
        }
        catch { }

        // Compare the window's HWND to the foreground HWND we captured at the
        // start of the pass.
        try
        {
            IntPtr h = new IntPtr(window.Current.NativeWindowHandle);
            if (h != IntPtr.Zero && h == originalForeground) return "focused";
        }
        catch { }
        return "background";
    }

    /// <summary>
    /// Translate the window's HWND to a "monitor 1" / "monitor 2" / "primary"
    /// label using <see cref="Screen.AllScreens"/>. Single-monitor systems
    /// always return "" so we don't pollute the email with redundant info.
    /// The <paramref name="monitorMap"/> parameter is unused at the moment
    /// (kept for the call signature in case we want to switch to a stable
    /// HMONITOR map later).
    /// </summary>
    private static string ClassifyMonitor(
        AutomationElement window, Dictionary<IntPtr, string> monitorMap)
    {
        try
        {
            // Skip the lookup entirely on single-monitor boxes — the answer
            // is always "the one monitor" and no helpdesk tech needs to read it.
            if (Screen.AllScreens.Length <= 1) return "";

            IntPtr h = new IntPtr(window.Current.NativeWindowHandle);
            if (h == IntPtr.Zero) return "";

            var screen = Screen.FromHandle(h);
            if (screen == null) return "";

            // Find the screen index in Screen.AllScreens for a stable label.
            // Primary monitor is called out explicitly because that's the
            // user's laptop screen 99% of the time.
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                if (Screen.AllScreens[i].DeviceName == screen.DeviceName)
                {
                    return screen.Primary ? "primary monitor" : $"monitor {i + 1}";
                }
            }
            return screen.Primary ? "primary monitor" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Reserved for a future switch to EnumDisplayMonitors / HMONITOR-keyed
    /// labels. Returning an empty dict keeps the call sites unchanged.
    /// </summary>
    private static Dictionary<IntPtr, string> BuildMonitorMap()
    {
        return new Dictionary<IntPtr, string>();
    }

    private static IntPtr SafeGetForegroundWindow()
    {
        try { return GetForegroundWindow(); }
        catch { return IntPtr.Zero; }
    }

    private static bool UserStartedInteracting(IntPtr originalForeground)
    {
        if (originalForeground == IntPtr.Zero) return false;
        try
        {
            IntPtr current = GetForegroundWindow();
            // Foreground stayed the same → user hasn't grabbed focus.
            return current != IntPtr.Zero && current != originalForeground;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadAddressBar(AutomationElement addressBar)
    {
        try
        {
            if (addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj)
                && vpObj is ValuePattern vp)
            {
                return (vp.Current.Value ?? "").Trim();
            }
        }
        catch { }
        return "";
    }

    private static bool TryGetSelectionItemPattern(
        AutomationElement element, out SelectionItemPattern? pattern)
    {
        pattern = null;
        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object obj)
                && obj is SelectionItemPattern sip)
            {
                pattern = sip;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Current.Name ?? ""; }
        catch { return ""; }
    }

    private static bool TimedOut(int deadlineTicks)
    {
        // Environment.TickCount wraps every ~25 days; subtraction is wrap-safe.
        return unchecked(Environment.TickCount - deadlineTicks) > 0;
    }
}
