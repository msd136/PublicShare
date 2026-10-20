using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GamGui.Core;

namespace GamGui.Views;

/// <summary>Type any GAM command, exactly as at a command prompt.</summary>
public sealed class ConsoleView : ViewBase
{
    private const int MaxLiveLines = 4000;

    private readonly HintBox _input = new("print users fields primaryemail,suspended", mono: true);
    private readonly HintBox _stdinBox = new("Reply to a GAM prompt, then press Enter");
    private readonly TextBox _live = new() { Style = Ui.Style("MonoText"), BorderThickness = new Thickness(0) };
    private readonly ResultView _results = new("Run a command; CSV output from print commands appears here as a table.");
    private readonly Button _run;
    private readonly Button _stop;
    private readonly Button _send;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _liveLines;
    private int _historyIndex = -1;
    private CancellationTokenSource? _cts;
    private StreamWriter? _stdin;

    public ConsoleView()
    {
        _run = Ui.Button("Run", async (_, _) => await RunInputAsync(), "PrimaryButton");
        _stop = Ui.Button("Stop", (_, _) => _cts?.Cancel());
        _stop.IsEnabled = false;
        _send = Ui.Button("Send", (_, _) => SendInput());
        _send.IsEnabled = false;
        _stdinBox.OnEnter(SendInput);
        _stdinBox.IsEnabled = false;
        _input.Box.PreviewKeyDown += OnInputKey;
        _flush.Tick += (_, _) => FlushLive();

        var gamLabel = new TextBlock
        {
            Text = "gam",
            FontFamily = (FontFamily)Ui.Res("MonoFont"),
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 6),
        };
        var inputRow = new DockPanel { LastChildFill = true };
        var inputButtons = Ui.Row(Ui.Margin(_run, 6, 0, 6, 6), _stop,
            Ui.Button("Open in terminal", (_, _) => OpenTerminal(),
                tip: "Runs in a console window, for commands that ask questions or open a browser."),
            Ui.Button("Clear", (_, _) => { _live.Clear(); _liveLines = 0; _results.ShowEmpty(); }));
        DockPanel.SetDock(gamLabel, Dock.Left);
        DockPanel.SetDock(inputButtons, Dock.Right);
        inputRow.Children.Add(gamLabel);
        inputRow.Children.Add(inputButtons);
        inputRow.Children.Add(Ui.Margin(_input, 0, 0, 0, 6));

        var stdinRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_send, Dock.Right);
        stdinRow.Children.Add(Ui.Margin(_send, 6, 0, 0, 0));
        stdinRow.Children.Add(_stdinBox);

        var liveBorder = new Border
        {
            BorderBrush = Ui.Brush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = _live,
        };

        var splitter = new GridSplitter
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Rows,
            Cursor = Cursors.SizeNS,
        };

        Content = Ui.Grid("auto,auto,auto,auto,*,6,2*").Add(
            Header("Console", "Type any GAM command. Up and Down recall earlier commands. Output streams live; CSV becomes a table when the command finishes."),
            inputRow.At(1),
            stdinRow.At(2),
            Ui.Text("Live output", "Muted", 12).At(3),
            liveBorder.At(4),
            splitter.At(5),
            _results.At(6));
    }

    /// <summary>Puts a command line in the input box (used by "Edit in console").</summary>
    public void SetCommand(string line)
    {
        _input.Text = line;
        _input.Box.CaretIndex = line.Length;
        Dispatcher.InvokeAsync(() => _input.Box.Focus(), DispatcherPriority.Input);
    }

    protected override void OnBusyChanged(bool busy)
    {
        _run.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        _send.IsEnabled = busy;
        _stdinBox.IsEnabled = busy;
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        var history = AppServices.Settings.ConsoleHistory;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_run.IsEnabled) _ = RunInputAsync();
                break;
            case Key.Up when history.Count > 0:
                e.Handled = true;
                _historyIndex = _historyIndex < 0 ? history.Count - 1 : Math.Max(0, _historyIndex - 1);
                _input.Text = history[_historyIndex];
                _input.Box.CaretIndex = _input.Text.Length;
                break;
            case Key.Down when history.Count > 0 && _historyIndex >= 0:
                e.Handled = true;
                _historyIndex++;
                if (_historyIndex >= history.Count) { _historyIndex = -1; _input.Text = ""; }
                else { _input.Text = history[_historyIndex]; _input.Box.CaretIndex = _input.Text.Length; }
                break;
        }
    }

    private async Task RunInputAsync()
    {
        var line = _input.Value;
        var args = ArgUtil.StripGam(ArgUtil.Split(line));
        if (args.Count == 0) return;
        Remember(line);

        var cmd = new GamCommand(args);
        _live.Clear();
        _liveLines = 0;
        while (_pending.TryDequeue(out _)) { }
        _flush.Start();
        _cts = new CancellationTokenSource();
        try
        {
            await RunAsync(cmd, _results,
                onLine: (text, _) => _pending.Enqueue(text),
                ct: _cts.Token,
                stdin: writer => _stdin = writer);
        }
        finally
        {
            _stdin = null;
            _cts.Dispose();
            _cts = null;
            _flush.Stop();
            FlushLive();
        }
    }

    private void Remember(string line)
    {
        var h = AppServices.Settings.ConsoleHistory;
        h.Remove(line);
        h.Add(line);
        _historyIndex = -1;
        AppServices.SaveSettings();
    }

    private void FlushLive()
    {
        if (_pending.IsEmpty) return;
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var l))
        {
            if (_liveLines >= MaxLiveLines)
            {
                if (_liveLines == MaxLiveLines) sb.Append("… live view truncated; the full output is in the panel below.\n");
                _liveLines++;
                continue;
            }
            sb.Append(l).Append('\n');
            _liveLines++;
        }
        if (sb.Length > 0)
        {
            _live.AppendText(sb.ToString());
            _live.ScrollToEnd();
        }
    }

    /// <summary>Interactive commands read stdin; this lets the user answer prompts.</summary>
    private void SendInput()
    {
        var text = _stdinBox.Text;
        _stdinBox.Text = "";
        _pending.Enqueue("> " + text);
        var writer = _stdin;
        if (writer == null) { _pending.Enqueue("No command is waiting for input."); return; }
        try { writer.WriteLine(text); writer.Flush(); }
        catch (Exception ex) { _pending.Enqueue("Couldn't send input: " + ex.Message); }
    }

    private void OpenTerminal()
    {
        var args = ArgUtil.StripGam(ArgUtil.Split(_input.Value));
        try { Runner.OpenInTerminal(new GamCommand(args)); }
        catch (Exception ex) { Dialogs.Error(OwnerWindow, ex.Message); }
    }
}
