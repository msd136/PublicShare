using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GamGui.Core;

namespace GamGui;

public static class Dialogs
{
    private const string AppTitle = "GAM Workbench";

    public static void Error(Window? owner, string message) =>
        Show(owner, message, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(Window? owner, string message) =>
        Show(owner, message, MessageBoxButton.OK, MessageBoxImage.Information);

    private static MessageBoxResult Show(Window? owner, string msg, MessageBoxButton buttons, MessageBoxImage image) =>
        owner != null && owner.IsVisible
            ? MessageBox.Show(owner, msg, AppTitle, buttons, image)
            : MessageBox.Show(msg, AppTitle, buttons, image);

    private static Window NewWindow(Window? owner, string title, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            FontFamily = (FontFamily)Ui.Res("UiFont"),
            FontSize = 13.5,
            Background = Ui.Brush("SurfaceBrush"),
            ShowInTaskbar = false,
        };
        if (owner != null && owner.IsVisible)
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w;
    }

    /// <summary>
    /// Shows what is about to happen in plain words, then the exact GAM command(s).
    /// Cancel is the default button so pressing Enter never runs a change by accident.
    /// </summary>
    public static bool Confirm(Window? owner, string what, IReadOnlyList<GamCommand> commands)
    {
        var w = NewWindow(owner, "Check before running", 640, 0);
        w.SizeToContent = SizeToContent.Height;
        w.ResizeMode = ResizeMode.NoResize;

        var lines = commands.Take(12).Select(c => c.Display).ToList();
        if (commands.Count > 12) lines.Add($"… and {commands.Count - 12} more");

        var cmdBox = new TextBox
        {
            Style = Ui.Style("MonoText"),
            Text = string.Join("\n", lines),
            MaxHeight = 180,
            TextWrapping = TextWrapping.Wrap,
        };

        bool ok = false;
        var run = Ui.Button(commands.Count > 1 ? $"Run {commands.Count} commands" : "Run", (_, _) => { ok = true; w.Close(); }, "PrimaryButton");
        var cancel = Ui.Button("Cancel", (_, _) => w.Close());
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        var buttons = Ui.Row(run, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 16, 0, 0);

        w.Content = new StackPanel
        {
            Margin = new Thickness(22, 18, 22, 14),
            Children =
            {
                Ui.Text("Check before running", "H1"),
                Ui.Margin(Ui.Text(what), 0, 6, 0, 14),
                Ui.Text(commands.Count > 1 ? "GAM will run these commands, one after another:" : "GAM will run this command:", "Muted", 12.5),
                Ui.Margin(cmdBox, 0, 4, 0, 6),
                Ui.Text("Nothing changes until you choose Run.", "Muted", 12.5),
                buttons,
            },
        };
        w.Loaded += (_, _) => cancel.Focus();
        w.ShowDialog();
        return ok;
    }

    /// <summary>Non-modal window with GAM's text output (for example "info user").</summary>
    public static void ShowText(Window? owner, string title, string text) => ShowOutput(owner, title, text, null);

    /// <summary>
    /// Shows the outcome of a run. If GAM's output matches a known problem, the explanation
    /// and fix appear above the raw output.
    /// </summary>
    public static void ShowResult(Window? owner, string title, GamResult result)
    {
        var text = (result.StdOut + (result.StdErr.Length > 0 ? "\n" + result.StdErr : "")).Trim();
        if (text.Length == 0) text = result.Success ? "Done. GAM returned no output." : "GAM returned no output.";
        var advice = ErrorAdvisor.Diagnose(result);
        if (advice == null && !result.Success && !title.Contains("problem", StringComparison.OrdinalIgnoreCase))
            title += " (GAM reported a problem)";
        ShowOutput(owner, title, text, advice);
    }

    private static void ShowOutput(Window? owner, string title, string text, ErrorAdvice? advice)
    {
        var w = NewWindow(owner, title, 780, advice != null ? 680 : 560);
        var box = new TextBox { Style = Ui.Style("MonoText"), Text = text, BorderThickness = new Thickness(0) };
        var copy = Ui.Button("Copy output", (_, _) => Ui.Copy(text));
        var close = Ui.Button("Close", (_, _) => w.Close());
        close.IsCancel = true;
        var bar = Ui.Row(copy, close);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Margin = new Thickness(12, 10, 6, 6);

        var grid = Ui.Grid("auto,auto,*,auto");
        if (advice != null)
        {
            var panel = new AdvicePanel();
            panel.Show(advice);
            grid.Add(Ui.Margin(panel, 12, 12, 12, 4).At(0));
            grid.Add(Ui.Margin(Ui.Text("What GAM printed", "Muted", 12), 14, 8, 0, 2).At(1));
        }
        grid.Add(box.At(2), bar.At(3));
        w.Content = grid;
        w.Show();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser registered */ }
    }
}

/// <summary>
/// Explains a recognized GAM or Google error: what it means, how to fix it, and a button
/// that runs the fix when there is one.
/// </summary>
public sealed class AdvicePanel : Border
{
    private ErrorAdvice? _advice;

    public AdvicePanel()
    {
        Background = Ui.Brush("WarnSoftBrush");
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0xC4, 0x6A));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(16, 12, 16, 8);
        Visibility = Visibility.Collapsed;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    public void Show(ErrorAdvice? advice)
    {
        _advice = advice;
        if (advice == null) { Hide(); return; }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = advice.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14.5,
            Foreground = Ui.Brush("InkBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(Ui.Margin(Ui.Text(advice.Explanation), 0, 4, 0, 8));
        stack.Children.Add(Ui.Text("How to fix it", "H2"));
        for (int i = 0; i < advice.Steps.Count; i++)
        {
            var row = Ui.Grid("auto", "24,*");
            row.Add(Ui.Text($"{i + 1}.").At(0, 0), Ui.Text(advice.Steps[i]).At(0, 1));
            row.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(row);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (advice.Fix != null)
        {
            var fix = advice.Fix;
            buttons.Children.Add(Ui.Button(fix.Label, (_, _) => FixRunner.Run(Window.GetWindow(this), fix), "PrimaryButton", fix.Explanation));
        }
        if (advice.HelpUrl != null)
            buttons.Children.Add(Ui.Button(advice.HelpLabel ?? "Read more", (_, _) => Dialogs.OpenUrl(advice.HelpUrl), tip: advice.HelpUrl));
        buttons.Children.Add(Ui.Button("Hide", (_, _) => Hide()));
        stack.Children.Add(buttons);

        if (advice.Fix != null)
            stack.Children.Add(Ui.Margin(Ui.Text(advice.Fix.Label + ": " + advice.Fix.Explanation, "Muted", 12), 0, 0, 0, 4));
        if (advice.MatchedLine.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Recognized from: " + advice.MatchedLine,
                FontFamily = (FontFamily)Ui.Res("MonoFont"),
                FontSize = 11.5,
                Foreground = Ui.Brush("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });
        }

        Child = stack;
        Visibility = Visibility.Visible;
    }
}

/// <summary>Runs the fix attached to a piece of advice.</summary>
public static class FixRunner
{
    public static void Run(Window? owner, FixAction fix)
    {
        var args = fix.Args.ToList();
        if (fix.NeedsUser)
        {
            var v = FormDialog.Ask(owner, fix.Label, fix.Explanation, "Continue",
                new Field("user", "A user in your domain", Required: true,
                    Hint: "Any active account works; GAM uses it to test access."));
            if (v == null) return;
            args = args.Select(a => a == "{user}" ? v["user"] : a).ToList();
        }

        var cmd = new GamCommand(args);
        try
        {
            if (fix.InTerminal)
            {
                AppServices.Runner.OpenInTerminal(cmd);
                Dialogs.Info(owner, "A console window opened and is running:\n\n" + cmd.Display +
                                    "\n\nFollow the prompts there. When GAM finishes, close that window and try your task again.");
            }
            else
            {
                _ = RunAndShow(owner, fix.Label, cmd);
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, ex.Message);
        }
    }

    private static async Task RunAndShow(Window? owner, string title, GamCommand cmd)
    {
        try
        {
            var r = await AppServices.Runner.RunAsync(cmd);
            Dialogs.ShowResult(owner, title, r);
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, ex.Message);
        }
    }
}
