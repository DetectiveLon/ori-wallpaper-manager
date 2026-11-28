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
    // One recurring failure can log on every layout pass. Never let that turn into
    // a stream of tray balloons; every failure is still written to the log.
    private static DateTimeOffset _lastErrorNoticeUtc;

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateThread;

    private const string InstanceMutexName = @"Local\Ori.WallpaperManager";
    private const string ActivateEventName = @"Local\Ori.WallpaperManager.Activate";

    // Error log: bounded rotation (1 MB x 3 files) with path/URL redaction.
    private const long MaxLogLength = 1_000_000;
    private const int MaxLogFiles = 3;

    private static readonly Regex UrlPattern = new(
        @"\b(?:https?|file)://\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Windows accepts either separator, and error text can contain C:/ paths.
    // URLs are replaced first (see Redact), so this never eats a scheme.
    private static readonly Regex WindowsPathPattern = new(
        @"[A-Za-z]:[\\/][^\s,;:)\]]+",
        RegexOptions.Compiled);
    // UNC shares (\\server\share\file) must be redacted too: a drive-letter-only
    // pattern leaks the server and share names into the diagnostics log.
    private static readonly Regex UncPathPattern = new(
        @"\\\\[^\s,;:)\]]+",
        RegexOptions.Compiled);

    /// <summary>Directory that holds the redacted diagnostics logs.</summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Ori lives in the notification area and keeps a rotation engine running, so a
        // second launch from the Start menu or Explorer must not start a second copy
        // that writes the same library, schedule and cache files.
        //
        // Create the activation event BEFORE the mutex check: otherwise a second
        // launch in that gap cannot open a handle and exits with no activation.
        try
        {
            _activateEvent = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception)
        {
            // A denied ACL must still let the single instance start; activation
            // from later launches is simply unavailable in that case.
            _activateEvent = null;
        }

        var isFirstInstance = false;
        try
        {
            _instanceMutex = new Mutex(
                initiallyOwned: true, InstanceMutexName, out isFirstInstance);
            // initiallyOwned does not grant ownership when the mutex already exists.
            if (!isFirstInstance && !TryReclaimInstanceMutex())
            {
                ReleaseInstanceHandles();
                SignalRunningInstance();
                Shutdown();
                return;
            }
            _ownsInstanceMutex = true;
        }
        catch (AbandonedMutexException)
        {
            // A hard-killed previous owner left the mutex abandoned. Ownership
            // transfers to this thread, so this launch is the live instance.
            _ownsInstanceMutex = true;
            try { _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName); }
            catch (Exception) { _instanceMutex = null; }
        }
        catch (Exception)
        {
            // An unusable mutex cannot be used to protect one instance; continue
            // without a guard rather than deadlocking on every launch.
            _instanceMutex = null;
        }

        StartActivationListener();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        Tray = new TrayIconService(OpenFromTray, ExitFromTray);
        Tray.Show("Ori — wallpaper manager");

        // App.xaml deliberately has no StartupUri: the window is created here so
        // the tray icon and rotation loops can run with the window hidden. WPF's
        // default ShutdownMode (OnLastWindowClose) is overridden in App.xaml with
        // OnExplicitShutdown, otherwise a never-shown MainWindow could end the
        // process. Tray "Exit" calls Shutdown explicitly.
        var startMinimized = UiPreferencesStore.Load().StartMinimized;
        var window = new MainWindow();
        Current.MainWindow = window;
        if (startMinimized)
        {
            window.ShowInTaskbar = false;
            // Leave the window hidden: no Show(). OpenFromTray restores it with
            // ShowInTaskbar=true, WindowState.Normal, Show and Activate.
            ShowFirstTrayHintOnce();
        }
        else
        {
            window.Show();
        }

        // Optional and bounded: a missing Ori.Updater.exe is a silent no-op and
        // the check never delays the window or the tray. The completion callback
        // refreshes a Settings page that is already open; OnLoaded remains the
        // fallback for a page opened after the check finished.
        UpdateChecker.Completed += OnUpdateCheckCompleted;
        var updateCheck = UpdateChecker.CheckOnStartupAsync();
        _ = ObserveUpdateCheckAsync(updateCheck);
    }

    /// <summary>
    /// Reclaims a named mutex that already existed. A process killed with Task
    /// Manager leaves the mutex abandoned; WaitOne(0) succeeds in that case and
    /// throws AbandonedMutexException, which also means this copy owns it.
    /// </summary>
    private bool TryReclaimInstanceMutex()
    {
        if (_instanceMutex is null)
            return false;
        try
        {
            try { return _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReleaseInstanceHandles()
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _ownsInstanceMutex = false;
        _activateEvent?.Dispose();
        _activateEvent = null;
    }

    /// <summary>Marshals an update-check completion to the live Settings view model.</summary>
    private void OnUpdateCheckCompleted()
    {
        void Refresh()
        {
            try { (Current.MainWindow as MainWindow)?.RefreshSettingsUpdateStatus(); }
            catch (Exception) { /* A status refresh must never break startup. */ }
        }

        try
        {
            // InvokeAsync is safe from the checker's worker thread and queues the
            // refresh on the dispatcher; no view is created or navigated to here.
            Dispatcher.InvokeAsync(Refresh);
        }
        catch (Exception)
        {
            // Dispatcher may already be shutting down.
        }
    }

    /// <summary>Observes the fire-and-forget check so its task is never unobserved.</summary>
    private static async Task ObserveUpdateCheckAsync(Task updateCheck)
    {
        try { await updateCheck.ConfigureAwait(false); }
        catch (Exception) { /* Optional check; never fatal. */ }

        var app = Current as App;
        app?.OnUpdateCheckCompleted();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Tray = null;
        _activateEvent?.Dispose();
        _activateEvent = null;
        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* Already released on a failed start. */ }
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _ownsInstanceMutex = false;
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

        // Already visible: never touch ShowInTaskbar or call Show again. Re-creating
        // the presentation source of a visible window is what can leave its root
        // visual parented and throw on the next restore.
        if (window.IsVisible)
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }

        try
        {
            window.ShowInTaskbar = true;
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
        catch (ArgumentException ex) when (
            ex.Message.Contains("root Visual", StringComparison.OrdinalIgnoreCase))
        {
            // A failed source creation can leave this window unusable. Keep Ori
            // usable by replacing it rather than leaving the user with no window.
            ReportFailure(ex);
            Current.MainWindow = null;
            var replacement = new MainWindow();
            Current.MainWindow = replacement;
            replacement.Show();
            replacement.Activate();
        }
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
    /// Wakes the running copy when a second launch happens, so the user gets the
    /// window they asked for instead of a silent no-op or a second rotation engine.
    /// </summary>
    private static void SignalRunningInstance()
    {
        // Brief retry covers a launch racing the running copy's event creation.
        // The event is normally already present because it is created before the
        // mutex, but a slow ACL/filesystem lookup must not lose the activation.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
                {
                    using (handle)
                        handle.Set();
                    return;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Nothing else can be done by the second copy.
                return;
            }

            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Listens for a second launch on a background thread and restores the main
    /// window on the dispatcher. Background, so it can never keep the process alive.
    /// </summary>
    private void StartActivationListener()
    {
        var handle = _activateEvent;
        if (handle is null)
            return;

        _activateThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (handle is null || !handle.WaitOne())
                        return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    Dispatcher.InvokeAsync(OpenFromTray);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
                {
                    return; // The dispatcher is shutting down.
                }
            }
        })
        {
            IsBackground = true,
            Name = "Ori activation listener"
        };
        _activateThread.Start();

        // AutoReset keeps a signal raised before the thread reaches WaitOne, but
        // replay one pending activation here as well so a very early second launch
        // cannot be lost in the listener's startup window.
        try
        {
            if (handle.WaitOne(0))
                Dispatcher.InvokeAsync(OpenFromTray);
        }
        catch (Exception)
        {
            // The listener thread handles signals from this point on.
        }
    }

    /// <summary>
    /// Shows the "still running in the tray" hint the first time the window is
    /// hidden — at startup for a start-minimized launch, or via X / Alt+F4.
    /// One shot per process; null-safe when the tray icon is not ready yet.
    /// </summary>
    internal static void ShowFirstTrayHintOnce()
    {
        if (_trayHintShown)
            return;
        _trayHintShown = true;
        try
        {
            Tray?.Notify(
                "Ori",
                "Ori is still running in the tray — click the icon to reopen.");
        }
        catch (Exception)
        {
            // A balloon must never prevent a window from hiding or starting.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Mark handled first: LogFailure must never be able to leave an
        // exception unhandled just because tray teardown threw.
        e.Handled = true;
        LogFailure(e.Exception);
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
    /// Records a failure from code that cannot let an exception escape (tray
    /// callbacks, shutdown). Never throws.
    /// </summary>
    internal static void ReportFailure(Exception exception) => LogFailure(exception);

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
        // Rate-limited: a bug that fires on every layout pass must not produce a
        // stream of notifications. The log above always records every failure.
        try
        {
            lock (LogGate)
            {
                if (DateTimeOffset.UtcNow - _lastErrorNoticeUtc < TimeSpan.FromMinutes(1))
                    return;
                _lastErrorNoticeUtc = DateTimeOffset.UtcNow;
            }

            // Tray calls happen during dispatcher teardown as well. A disposed
            // NotifyIcon or a shutting-down dispatcher must not mask the original
            // failure or crash the error handler.
            Tray?.Notify(
                "Ori",
                "Something went wrong. Details were saved to the diagnostic log.",
                error: true);
            Tray?.UpdateTooltip("Ori — an error was logged (hover for details)");
        }
        catch (Exception)
        {
            // The log above is the authoritative record; notifications are best-effort.
        }
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

    /// <summary>
    /// Removes URLs, drive-letter paths and UNC shares from diagnostic text,
    /// keeping only the final file name so a log still identifies the image.
    /// Public so other components (and tests) use one redaction policy.
    /// </summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = UrlPattern.Replace(value, "[url]");
        return UncPathPattern.Replace(
            WindowsPathPattern.Replace(text, RedactPathMatch),
            RedactPathMatch);
    }

    private static string RedactPathMatch(Match match)
    {
        var raw = match.Value;
        var slash = raw.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 && slash + 1 < raw.Length
            ? raw[(slash + 1)..]
            : "[path]";
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
