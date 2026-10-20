using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GamGui.Core;

namespace GamGui;

/// <summary>Shared plumbing for every screen: running GAM, confirmations, and busy state.</summary>
public abstract class ViewBase : UserControl
{
    private int _busy;

    protected ViewBase()
    {
        Padding = new Thickness(24, 20, 24, 16);
    }

    protected Window? OwnerWindow => Window.GetWindow(this);

    protected static GamRunner Runner => AppServices.Runner;

    /// <summary>Screen heading plus a one-line description.</summary>
    protected static StackPanel Header(string title, string description)
    {
        var top = Ui.Grid("auto", "*,auto");
        top.Add(Ui.Stack(Ui.Text(title, "H1"), Ui.Text(description, "Muted")).At(0, 0));
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(top);

        var guide = Guides.For(title);
        if (guide == null) return header;

        // The guide is shown until the user hides it; the choice is remembered per screen.
        var hidden = AppServices.Settings.HiddenGuides.Contains(title);
        var panel = new ScrollViewer
        {
            Content = new GuidePanel(guide),
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = hidden ? Visibility.Collapsed : Visibility.Visible,
        };
        var toggle = new Button
        {
            Content = hidden ? "Show guide" : "Hide guide",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 2, 0, 0),
            ToolTip = "Step-by-step help for this screen",
        };
        toggle.Click += (_, _) =>
        {
            var show = panel.Visibility != Visibility.Visible;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = show ? "Hide guide" : "Show guide";
            var list = AppServices.Settings.HiddenGuides;
            list.Remove(title);
            if (!show) list.Add(title);
            AppServices.SaveSettings();
        };
        top.Add(toggle.At(0, 1));
        header.Children.Add(panel);
        return header;
    }

    protected bool IsBusy => _busy > 0;

    /// <summary>Called when a run starts or finishes so screens can disable buttons.</summary>
    protected virtual void OnBusyChanged(bool busy) { }

    /// <summary>
    /// Runs one GAM command. When <paramref name="confirm"/> is given, the exact command is shown
    /// and must be approved first. Returns null if cancelled or GAM could not start.
    /// </summary>
    protected async Task<GamResult?> RunAsync(
        GamCommand cmd,
        ResultView? output = null,
        string? confirm = null,
        Action<string, bool>? onLine = null,
        CancellationToken ct = default,
        Action<StreamWriter?>? stdin = null)
    {
        if (confirm != null && !Dialogs.Confirm(OwnerWindow, confirm, new[] { cmd })) return null;
        output?.ShowRunning(cmd.Display);
        SetBusy(+1);
        try
        {
            var result = await Runner.RunAsync(cmd, onLine, ct, stdin);
            output?.ShowResult(result, cmd.ExpectsCsv);
            return result;
        }
        catch (Exception ex)
        {
            output?.ShowText(ex.Message, true);
            Dialogs.Error(OwnerWindow, ex.Message);
            return null;
        }
        finally
        {
            SetBusy(-1);
        }
    }

    /// <summary>Runs commands one after another and reports each outcome. Returns true if all succeeded.</summary>
    protected async Task<bool> RunManyAsync(IReadOnlyList<GamCommand> cmds, string confirm, bool showReport = true)
    {
        if (cmds.Count == 0) return true;
        if (!Dialogs.Confirm(OwnerWindow, confirm, cmds)) return false;
        var report = new StringBuilder();
        bool allOk = true;
        SetBusy(+1);
        try
        {
            foreach (var c in cmds)
            {
                var r = await Runner.RunAsync(c);
                allOk &= r.Success;
                report.AppendLine((r.Success ? "OK      " : "FAILED  ") + c.Display);
                var detail = (r.StdOut + r.StdErr).Trim();
                if (detail.Length > 0)
                    report.AppendLine("        " + detail.Replace("\n", "\n        ")).AppendLine();
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(OwnerWindow, ex.Message);
            return false;
        }
        finally
        {
            SetBusy(-1);
        }
        if (showReport || !allOk)
            Dialogs.ShowResult(OwnerWindow, allOk ? "Finished" : "Finished with errors",
                new GamResult { ExitCode = allOk ? 0 : 1, StdOut = report.ToString() });
        return allOk;
    }

    /// <summary>Runs a command and shows its text output in a separate window.</summary>
    protected async Task ShowTextOf(GamCommand cmd, string title)
    {
        var r = await RunAsync(cmd);
        if (r == null) return;
        Dialogs.ShowResult(OwnerWindow, title, r);
    }

    private void SetBusy(int delta)
    {
        bool before = _busy > 0;
        _busy = Math.Max(0, _busy + delta);
        bool after = _busy > 0;
        if (before != after) OnBusyChanged(after);
    }

    /// <summary>Picks rows or explains how to.</summary>
    protected List<string> RequireSelection(ResultView view, string noun, params string[] columns)
    {
        var values = view.SelectedValues(columns);
        if (values.Count == 0)
            Dialogs.Info(OwnerWindow, view.Table == null
                ? $"Load the list first, then select one or more {noun}."
                : $"Select one or more {noun} in the list first.");
        return values;
    }
}
