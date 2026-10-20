using System.Diagnostics;
using System.Text;

namespace GamGui.Core;

/// <summary>A GAM invocation. Arguments exclude the leading "gam".</summary>
public sealed class GamCommand
{
    public GamCommand(IEnumerable<string> args, IEnumerable<int>? redact = null)
    {
        Args = args.ToList();
        Redact = new HashSet<int>(redact ?? Array.Empty<int>());
    }

    public static GamCommand Of(params string[] args) => new(args);

    public List<string> Args { get; }

    /// <summary>Argument indexes (e.g. passwords) shown as ******** in the UI and history.</summary>
    public HashSet<int> Redact { get; }

    /// <summary>The command as shown to the user, with secrets masked.</summary>
    public string Display =>
        "gam " + string.Join(" ", Args.Select((a, i) => Redact.Contains(i) ? "********" : ArgUtil.Quote(a)));

    /// <summary>True for commands that change or remove data; the GUI confirms these first.</summary>
    public bool LooksDestructive
    {
        get
        {
            var words = Args.Select(a => a.ToLowerInvariant()).ToHashSet();
            string[] risky =
            {
                "delete", "del", "remove", "purge", "wipe", "empty", "deprovision", "clear", "sync",
                "transfer", "undelete", "suspend", "signout", "turnoff2sv", "admin_remote_wipe",
                "admin_account_wipe", "accountwipe", "wipeaccount", "trash", "untrash", "move", "update",
                "create", "add", "replace", "deleteall", "deprovision_retiring_device", "doit"
            };
            return risky.Any(words.Contains) || words.Any(w => w.StartsWith("deprovision"));
        }
    }

    /// <summary>"print" and "report" style commands write CSV to stdout.</summary>
    public bool ExpectsCsv
    {
        get
        {
            var lower = Args.Select(a => a.ToLowerInvariant()).ToList();
            if (lower.Contains("formatjson") && !lower.Contains("print")) return false;
            return lower.Contains("print") || lower.Contains("report") || lower.Contains("csvoutput");
        }
    }
}

public sealed class GamResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool Cancelled { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Success => ExitCode == 0 && !Cancelled;
}

public sealed class GamRunner
{
    private readonly Func<AppSettings> _settings;

    public GamRunner(Func<AppSettings> settings) => _settings = settings;

    /// <summary>Raised on the calling thread's sync context is NOT guaranteed; UI must marshal.</summary>
    public event Action<GamCommand>? Started;
    public event Action<GamCommand, GamResult>? Finished;

    public string? ResolveGamPath() => GamLocator.Find(_settings().GamPath);

    /// <param name="onLine">Called for each output line; the bool is true for stderr.</param>
    /// <param name="stdin">Optional writer callback that receives the process stdin (console use). If null, stdin is closed.</param>
    public async Task<GamResult> RunAsync(
        GamCommand command,
        Action<string, bool>? onLine = null,
        CancellationToken ct = default,
        Action<StreamWriter?>? stdin = null)
    {
        var gam = ResolveGamPath() ?? throw new FileNotFoundException(
            "gam.exe was not found. Set its location in Settings.");

        var psi = new ProcessStartInfo
        {
            FileName = gam,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(gam) ?? Environment.CurrentDirectory,
        };
        foreach (var a in command.Args) psi.ArgumentList.Add(a);
        ApplyEnvironment(psi.Environment, _settings());

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdout) stdout.Append(e.Data).Append('\n');
            onLine?.Invoke(e.Data, false);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) stderr.Append(e.Data).Append('\n');
            onLine?.Invoke(e.Data, true);
        };

        Started?.Invoke(command);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (stdin != null) stdin(proc.StandardInput);
        else proc.StandardInput.Close();

        bool cancelled = false;
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        // Ensure async readers have drained.
        try { proc.WaitForExit(); } catch { }
        stdin?.Invoke(null);
        sw.Stop();

        GamResult result;
        lock (stdout) lock (stderr)
            result = new GamResult
            {
                ExitCode = cancelled ? -1 : proc.ExitCode,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString(),
                Cancelled = cancelled,
                Elapsed = sw.Elapsed,
            };
        Finished?.Invoke(command, result);
        return result;
    }

    /// <summary>
    /// Opens GAM in a real console window. Needed for interactive commands such as
    /// "gam oauth create" or "gam create project" that prompt and open a browser.
    /// </summary>
    public void OpenInTerminal(GamCommand command)
    {
        var gam = ResolveGamPath() ?? throw new FileNotFoundException(
            "gam.exe was not found. Set its location in Settings.");
        var line = ArgUtil.Quote(gam) + (command.Args.Count > 0 ? " " + ArgUtil.Join(command.Args) : "");
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // cmd /k strips the outer quotes, leaving the quoted exe path intact.
            Arguments = "/k \"" + line + "\"",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(gam) ?? Environment.CurrentDirectory,
        };
        ApplyEnvironment(psi.Environment, _settings());
        Process.Start(psi);
    }

    private static void ApplyEnvironment(IDictionary<string, string?> env, AppSettings s)
    {
        env["PYTHONIOENCODING"] = "utf-8";
        env["PYTHONUTF8"] = "1";
        if (!string.IsNullOrWhiteSpace(s.GamConfigDir)) env["GAMCFGDIR"] = s.GamConfigDir;
    }
}
