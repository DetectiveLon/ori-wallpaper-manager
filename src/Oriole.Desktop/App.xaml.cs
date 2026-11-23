using System.IO;
using System.Windows;
using System.Windows.Threading;
using Oriole.Desktop.Services;

namespace Oriole.Desktop;

public partial class App : Application
{
    public static TrayIconService? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        Tray = new TrayIconService(OpenFromTray, ExitFromTray);
        Tray.Show("Oriole — wallpaper manager");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Tray = null;
        base.OnExit(e);
    }

    private static void OpenFromTray()
    {
        var window = Current.MainWindow;
        if (window is null)
        {
            window = new MainWindow();
            Current.MainWindow = window;
        }

        window.ShowInTaskbar = true;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private static void ExitFromTray() => Current.Shutdown();

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFailure(e.Exception);
        // Keep the app alive instead of silently exiting behind an async void handler.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            LogFailure(exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFailure(e.Exception);
        e.SetObserved();
    }

    private static void LogFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Oriole", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "errors.log"),
                $"{DateTimeOffset.UtcNow:O} {exception}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never become the reason the process dies.
        }
    }
}
