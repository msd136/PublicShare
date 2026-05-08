using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace HelpdeskHeroes;

/// <summary>
/// Centralised access to the embedded HelpdeskHeroes.ico so every Form's
/// titlebar / taskbar / Alt-Tab thumbnail matches the exe and the desktop
/// shortcut. The .ico is referenced as &lt;ApplicationIcon&gt; (which brands the
/// exe header) AND as an EmbeddedResource (which lets us hand the same bytes
/// to <see cref="Form.Icon"/> at runtime — no separate file needed after
/// publish).
///
/// Lookup is failure-tolerant: if the resource ever goes missing we fall back
/// to the system default rather than crashing on startup.
/// </summary>
internal static class Branding
{
    private const string ResourceName = "HelpdeskHeroes.ico";

    /// <summary>
    /// Cached raw bytes of the embedded icon. We hold the bytes — not an Icon
    /// instance — because Form.Icon assignments transfer ownership: if two
    /// forms shared one Icon, the first to close would Dispose() it and the
    /// second's titlebar would go blank (or throw on repaint). Keeping bytes
    /// + minting a fresh Icon per Apply() call sidesteps that entirely.
    /// </summary>
    private static byte[]? _cachedBytes;
    private static bool _bytesLoaded;

    private static byte[]? LoadBytes()
    {
        if (_bytesLoaded) return _cachedBytes;
        _bytesLoaded = true;
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            // Prefer an exact-name match; fall back to a suffix match in case
            // MSBuild ever prepends the default namespace.
            string? name = ResourceName;
            if (Array.IndexOf(asm.GetManifestResourceNames(), name) < 0)
            {
                name = null;
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith(ResourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        name = n;
                        break;
                    }
                }
            }

            if (name == null) return null;

            using Stream? s = asm.GetManifestResourceStream(name);
            if (s == null) return null;

            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _cachedBytes = ms.ToArray();
            return _cachedBytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mint a fresh <see cref="Icon"/> from the embedded bytes. Caller takes
    /// ownership — typically by assigning to <see cref="Form.Icon"/>, after
    /// which WinForms disposes it with the form. Returns null if the resource
    /// is missing or unreadable.
    /// </summary>
    public static Icon? CreateAppIcon()
    {
        try
        {
            var bytes = LoadBytes();
            if (bytes == null || bytes.Length == 0) return null;
            using var ms = new MemoryStream(bytes, writable: false);
            return new Icon(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Apply the branded icon to a form. Safe to call from a Form constructor —
    /// silently no-ops if the icon couldn't be loaded.
    /// </summary>
    public static void Apply(Form form)
    {
        try
        {
            var ico = CreateAppIcon();
            if (ico != null) form.Icon = ico;
            form.ShowIcon = true;
        }
        catch
        {
            // Icon assignment can throw on weird GDI states — never let branding
            // take down the form.
        }
    }
}
