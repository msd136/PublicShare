using System.Windows;
using System.Windows.Threading;
using GamGui.Core;

namespace GamGui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var text = e.Exception.Message + "\n\n" + e.Exception.GetType().Name;
        if (MainWindow != null && MainWindow.IsVisible)
            MessageBox.Show(MainWindow, text, "GAM Workbench", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(text, "GAM Workbench", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

/// <summary>Process-wide services shared by every screen.</summary>
public static class AppServices
{
    public static AppSettings Settings { get; } = AppSettings.Load();

    public static GamRunner Runner { get; } = new(() => Settings);

    private static CommandCatalog? _catalog;

    public static CommandCatalog Catalog =>
        _catalog ??= CommandCatalog.LoadBest(Settings.CommandsFile, Runner.ResolveGamPath());

    public static void ReloadCatalog() => _catalog = null;

    /// <summary>Raised when a screen wants to hand a command line to the Console screen.</summary>
    public static event Action<string>? OpenInConsoleRequested;

    public static void OpenInConsole(string commandLine) => OpenInConsoleRequested?.Invoke(commandLine);

    /// <summary>Raised when a screen links to another screen by its title, e.g. "Users".</summary>
    public static event Action<string>? NavigateRequested;

    public static void Navigate(string screen) => NavigateRequested?.Invoke(screen);

    public static void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show("Settings could not be saved: " + ex.Message, "GAM Workbench",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
