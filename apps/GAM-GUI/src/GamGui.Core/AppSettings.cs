using System.Security.Cryptography;
using System.Text.Json;

namespace GamGui.Core;

public sealed class AppSettings
{
    /// <summary>Full path to gam.exe. Empty means auto-detect.</summary>
    public string GamPath { get; set; } = "";

    /// <summary>Optional GAMCFGDIR (folder holding gam.cfg, oauth2.txt, oauth2service.json).</summary>
    public string GamConfigDir { get; set; } = "";

    /// <summary>Optional explicit GamCommands.txt. Empty means "next to gam.exe, else built-in".</summary>
    public string CommandsFile { get; set; } = "";

    public List<string> ConsoleHistory { get; set; } = new();
    /// <summary>Screens whose "How to" guide the user has hidden.</summary>
    public List<string> HiddenGuides { get; set; } = new();

    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamGui");

    public static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file shouldn't stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        if (ConsoleHistory.Count > 200) ConsoleHistory = ConsoleHistory.TakeLast(200).ToList();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

public static class GamLocator
{
    /// <summary>Returns the first gam.exe found, preferring the configured path.</summary>
    public static string? Find(string? configured)
    {
        foreach (var c in Candidates(configured))
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); }
            catch { /* invalid path */ }
        }
        return null;
    }

    public static IEnumerable<string> Candidates(string? configured)
    {
        var exe = OperatingSystem.IsWindows() ? "gam.exe" : "gam";
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured.Trim().Trim('"');
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "gam7", exe);
        yield return Path.Combine(baseDir, "gam", exe);
        yield return Path.Combine(baseDir, exe);
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\GAM7\gam.exe";
            yield return @"C:\GAM\gam.exe";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GAM7", "gam.exe");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GAM7", "gam.exe");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "bin", "gam7", "gam");
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir.Trim('"'), exe);
    }
}

public static class Passwords
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%*-_+=?";

    /// <summary>A random password with at least one of each character class (ambiguous characters removed).</summary>
    public static string Generate(int length = 16)
    {
        length = Math.Max(length, 8);
        var all = Upper + Lower + Digits + Symbols;
        var chars = new List<char>
        {
            Upper[RandomNumberGenerator.GetInt32(Upper.Length)],
            Lower[RandomNumberGenerator.GetInt32(Lower.Length)],
            Digits[RandomNumberGenerator.GetInt32(Digits.Length)],
            Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)],
        };
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        // Fisher-Yates shuffle
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
