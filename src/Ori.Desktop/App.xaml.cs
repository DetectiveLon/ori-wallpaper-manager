using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Ori.Desktop.Services;

namespace Ori.Desktop;

public partial class App : Application
{
    public static TrayIconService? Tray { get; private set; }

    /// <summary>
    /// Set only when the user chose "Exit" from the tray menu. MainWindow checks
    /// this in its Closing handler: a normal close (the X button / Alt+F4) cancels
    /// and hides to the tray, while an explicit exit is allowed to close and runs
    /// the bounded rotation-loop cleanup before shutdown.
    /// </summary>
    public static bool ExplicitExit { get; private set; }

    private static bool _trayHintShown;
    private static readonly object LogGate = new();

    // Error log: bounded rotation (1 MB x 3 files) with path/URL redaction.
    private const long MaxLogLength = 1_000_000;
    private const int MaxLogFiles = 3;

    private static readonly Regex UrlPattern = new(
        @"\b(?:https?|file)://\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPathPattern = new(
        @"[A-Za-z]:\\[^\s,;:)\]]+",
        RegexOptions.Compiled);

    /// <summary>Directory that holds the redacted diagnostics logs.</summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        Tray = new TrayIconService(OpenFromTray, ExitFromTray);
        Tray.Show("Ori — wallpaper manager");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Tray = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Restores the main window from the tray. Idempotent: a single tray click and
    /// a tray double-click may both call this, which is safe.
    /// </summary>
    private static void OpenFromTray()
    {
        var window = Current.MainWindow;
        if (window is null)
        {
            window = new MainWindow();
            Current.MainWindow = window;
        }

        window.ShowInTaskbar = true;
        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private static async void ExitFromTray()
    {
        // Mark the shutdown as user-requested so MainWindow does not hide to the
        // tray. Stop rotation loops up front without blocking the dispatcher;
        // MainWindow's Closing handler calls the synchronous routine again as a
        // fallback in case a hidden window's Closing event is not raised.
        ExplicitExit = true;
        try
        {
            if (Current.MainWindow is MainWindow window)
                await window.StopRotationForShutdownAsync();
        }
        catch (Exception)
        {
            // Shutdown must finish even if loop cleanup fails.
        }
        finally
        {
            Current.Shutdown();
        }
    }

    /// <summary>
    /// Shows the "still running in the tray" hint the first time the window is
    /// hidden via X / Alt+F4. One shot per process; null-safe when the tray icon
    /// has not been created yet.
    /// </summary>
    internal static void ShowFirstTrayHintOnce()
    {
        if (_trayHintShown)
            return;
        _trayHintShown = true;
        Tray?.Notify(
            "Ori",
            "Ori is still running in the tray — click the icon to reopen.");
    }

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

    /// <summary>
    /// Records a failure with bounded rotation and redaction, then acknowledges
    /// it non-intrusively through the tray. Never throws.
    /// </summary>
    private static void LogFailure(Exception exception)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, "errors.log");
                RotateIfNeeded(path);
                File.AppendAllText(path, FormatFailure(exception));
            }
        }
        catch
        {
            // Logging must never become the reason the process dies.
        }

        // Non-intrusive, user-visible notice. Tray may be null during startup.
        Tray?.Notify(
            "Ori",
            "Something went wrong. Details were saved to the diagnostic log.",
            error: true);
        Tray?.UpdateTooltip("Ori — an error was logged (hover for details)");
    }

    /// <summary>
    /// Short, safe summary the Settings view can surface without reading the raw
    /// log files. Paths, URLs and long messages are redacted/bounded.
    /// </summary>
    public static string GetDiagnosticsSummary()
    {
        try
        {
            var path = Path.Combine(LogDirectory, "errors.log");
            if (!File.Exists(path))
                return "No errors have been recorded.";

            var fileCount = Directory.GetFiles(LogDirectory, "errors*.log").Length;
            var sizeKb = new FileInfo(path).Length / 1024;
            var last = File.ReadLines(path).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            var hint = last is null ? string.Empty : Redact(last);
            if (hint.Length > 160)
                hint = hint[..160] + "…";
            return $"{fileCount} log file(s); current {sizeKb} KB. Most recent: {hint}";
        }
        catch
        {
            return "Diagnostics are unavailable right now.";
        }
    }

    /// <summary>
    /// Shifts errors.log -> errors.1.log -> ... up to <see cref="MaxLogFiles"/>
    /// total files, deleting the oldest. Caller holds <see cref="LogGate"/>.
    /// </summary>
    private static void RotateIfNeeded(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < MaxLogLength)
            return;

        var directory = file.DirectoryName;
        if (string.IsNullOrEmpty(directory))
            return;

        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        var oldest = Path.Combine(directory, $"{name}.{MaxLogFiles - 1}{extension}");
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var index = MaxLogFiles - 1; index >= 2; index--)
        {
            var source = Path.Combine(directory, $"{name}.{index - 1}{extension}");
            var target = Path.Combine(directory, $"{name}.{index}{extension}");
            if (!File.Exists(source))
                continue;
            if (File.Exists(target))
                File.Delete(target);
            File.Move(source, target);
        }

        var first = Path.Combine(directory, $"{name}.1{extension}");
        if (File.Exists(first))
            File.Delete(first);
        File.Move(path, first);
    }

    private static string FormatFailure(Exception exception)
    {
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var message = Redact(exception.Message);
        if (message.Length > 2000)
            message = message[..2000] + "…";

        var frames = (exception.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(8)
            .Select(frame => "   " + Redact(frame.Trim()));
        var stack = string.Join(Environment.NewLine, frames);

        return $"{DateTimeOffset.UtcNow:O} {type}: {message}{Environment.NewLine}{stack}{Environment.NewLine}";
    }

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = UrlPattern.Replace(value, "[url]");
        return WindowsPathPattern.Replace(text, match =>
        {
            var raw = match.Value;
            var slash = raw.LastIndexOf('\\');
            return slash >= 0 && slash + 1 < raw.Length
                ? raw[(slash + 1)..]
                : "[path]";
        });
    }
}

/// <summary>
/// Multi-value converter used by NavButtonStyle's single selection trigger.
/// Returns true when a navigation button's Tag matches the shell's currently
/// selected navigation key. values[0] = SelectedItem.Key, values[1] = button Tag.
/// </summary>
public sealed class NavKeyMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
            return false;
        var selected = values[0] as string;
        var key = values[1] as string;
        return string.Equals(selected, key, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
