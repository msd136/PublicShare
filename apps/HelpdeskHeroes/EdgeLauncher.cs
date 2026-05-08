using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using System.Windows.Forms;

namespace HelpdeskHeroes;

/// <summary>
/// Opens URLs strictly in Microsoft Edge. The deployment policy this app
/// targets is "Edge for every link" — public FAQ, internal knowledge base,
/// etc. — because Edge is typically the only browser configured for SSO,
/// content filtering, and your M365 tenant. Falling back to "default browser"
/// silently could land the user in Chrome on a personal Google account
/// and produce a confusing wrong-tenant experience.
///
/// Resolution order:
///   1. <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe</c>
///      — the canonical install-path registration. Set by every Edge installer.
///   2. <c>HKLM\SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command</c>
///      — the older Default Programs registration. Survives some uninstall
///      / repair scenarios where App Paths is missing but the EXE is still
///      on disk.
///   3. Well-known per-machine install paths under Program Files (x86) and
///      Program Files. Final safety net.
///
/// If all three miss, we surface a small dialog with the URL in a copy-friendly
/// box rather than silently invoking the default browser. Helpdesk would
/// rather hear "Edge is missing on this laptop" than chase a phantom
/// "Chrome opened the wrong account" ticket.
/// </summary>
internal static class EdgeLauncher
{
    /// <summary>Cache the resolved path for the lifetime of the process.</summary>
    private static string? _cachedPath;
    private static bool _resolveAttempted;

    /// <summary>
    /// Open <paramref name="url"/> in Edge. Pass <paramref name="owner"/> so
    /// the "Edge missing" error dialog (rare) is parented correctly.
    /// </summary>
    public static void Open(string url, IWin32Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string? edge = ResolveEdgePath();
        if (edge != null)
        {
            try
            {
                // UseShellExecute = false here, with explicit FileName=edge,
                // so we never accidentally route through the protocol handler
                // (which can be hijacked back to a different default browser
                // on misconfigured devices).
                Process.Start(new ProcessStartInfo
                {
                    FileName        = edge,
                    Arguments       = QuoteUrl(url),
                    UseShellExecute = false
                });
                return;
            }
            catch
            {
                // EXE present but launch failed (corrupt install, AV block,
                // etc.). Fall through to the missing-Edge dialog so the
                // user isn't left with a silent no-op.
            }
        }

        ShowMissingEdgeDialog(url, owner);
    }

    /// <summary>
    /// Walk the registry / well-known paths once. Cached for the rest of
    /// the session because Edge isn't going to install or uninstall while
    /// the user has the wizard open.
    /// </summary>
    private static string? ResolveEdgePath()
    {
        if (_resolveAttempted) return _cachedPath;
        _resolveAttempted = true;

        // 1. App Paths — canonical
        string? p = ReadAppPathDefault(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
        if (FileExists(p)) { _cachedPath = p; return p; }

        // 2. StartMenuInternet — older Default Programs registration. The
        //    value is a quoted command line (e.g. <c>"C:\…\msedge.exe" --foo</c>),
        //    so we have to extract just the EXE path.
        p = ExtractExePath(ReadAppPathDefault(
            @"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command"));
        if (FileExists(p)) { _cachedPath = p; return p; }

        // 3. Well-known install paths
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string candidate in new[]
        {
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf,   @"Microsoft\Edge\Application\msedge.exe")
        })
        {
            if (File.Exists(candidate)) { _cachedPath = candidate; return candidate; }
        }

        return null;
    }

    /// <summary>
    /// Read the (Default) value of an HKLM key — that's where App Paths
    /// stores the EXE path and where shell\open\command stores the launch
    /// command. Returns null on any failure.
    /// </summary>
    private static string? ReadAppPathDefault(string subKeyPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(null) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// shell\open\command stores values like <c>"C:\Program Files (x86)\…\msedge.exe" --single-argument %1</c>.
    /// Pull just the path out — handle both quoted and unquoted forms.
    /// </summary>
    private static string? ExtractExePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith("\""))
        {
            int closing = commandLine.IndexOf('"', 1);
            return closing > 1 ? commandLine.Substring(1, closing - 1) : null;
        }

        // Unquoted form: take everything up to the first space.
        int sp = commandLine.IndexOf(' ');
        return sp > 0 ? commandLine.Substring(0, sp) : commandLine;
    }

    private static bool FileExists(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    /// <summary>
    /// Quote the URL so a stray space (rare, but possible in some intranet
    /// link formats) doesn't get split into a second argument.
    /// </summary>
    private static string QuoteUrl(string url) => $"\"{url}\"";

    /// <summary>
    /// Tell the user we couldn't find Edge and give them the URL in a
    /// copy-friendly read-only textbox so they can paste it into another
    /// browser themselves. Strict per spec: we do NOT silently fall back
    /// to the default browser.
    /// </summary>
    private static void ShowMissingEdgeDialog(string url, IWin32Window? owner)
    {
        try
        {
            using var dlg = new Form
            {
                Text            = "Microsoft Edge not found",
                StartPosition   = owner != null
                                    ? FormStartPosition.CenterParent
                                    : FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox     = false,
                MinimizeBox     = false,
                ShowInTaskbar   = false,
                ClientSize      = new Size(520, 200),
                BackColor       = Color.White,
                Font            = new Font("Segoe UI", 10F)
            };
            Branding.Apply(dlg);

            var msg = new Label
            {
                Text = "We couldn't find Microsoft Edge on this device. Please copy " +
                       "this link into a browser, or ask the helpdesk to reinstall Edge.",
                AutoSize = false,
                Dock     = DockStyle.Top,
                Height   = 56,
                Padding  = new Padding(20, 16, 20, 4)
            };

            var urlBox = new TextBox
            {
                Text       = url,
                ReadOnly   = true,
                Font       = new Font("Consolas", 9.5F),
                BackColor  = Color.FromArgb(245, 247, 250),
                Width      = 480,
                Dock       = DockStyle.Top,
                Margin     = new Padding(20, 0, 20, 0)
            };
            // Pre-select for easy Ctrl+C
            urlBox.Enter += (_, _) => urlBox.SelectAll();

            var ok = new Button
            {
                Text      = "OK",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                Width     = 100,
                Height    = 36,
                Cursor    = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += (_, _) => dlg.Close();

            var btnRow = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height        = 56,
                Padding       = new Padding(20, 12, 20, 12),
                BackColor     = Color.FromArgb(245, 247, 250)
            };
            btnRow.Controls.Add(ok);

            // Add Fill placeholder, then docked controls in reverse order.
            dlg.Controls.Add(new Panel { Dock = DockStyle.Fill });
            dlg.Controls.Add(btnRow);
            dlg.Controls.Add(urlBox);
            dlg.Controls.Add(msg);
            dlg.AcceptButton = ok;
            dlg.CancelButton = ok;

            if (owner != null) dlg.ShowDialog(owner); else dlg.ShowDialog();
        }
        catch
        {
            // Truly degenerate case — even our error dialog blew up. Last
            // ditch: a plain MessageBox.
            try
            {
                MessageBox.Show(
                    owner,
                    $"Edge not found. Please copy this link into a browser:\r\n\r\n{url}",
                    "Microsoft Edge not found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch { /* nothing left to do */ }
        }
    }
}
