namespace Ori.Desktop.Services;

/// <summary>Result of one display-enumeration attempt, without throwing at callers.</summary>
public sealed record DisplayDetectionResult(
    IReadOnlyList<DisplayMonitor> Monitors,
    string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Single entry point for connected-display detection. Every page uses this so a
/// failure is presented consistently and no single caller can swallow it silently.
/// </summary>
public static class DisplayDetection
{
    private static EventHandler? _displaysChanged;
    private static int _systemHookInstalled;

    /// <summary>
    /// Raised when Windows reports a display/layout change. Subscribers must not
    /// assume the sender is meaningful; each handler is isolated so one bad
    /// subscriber cannot break hotplug for the rest of the app.
    /// </summary>
    public static event EventHandler? DisplaysChanged
    {
        add
        {
            if (value is null)
                return;

            _displaysChanged += value;
            if (Interlocked.Exchange(ref _systemHookInstalled, 1) == 0)
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
        }
        remove
        {
            if (value is null)
                return;

            _displaysChanged -= value;
        }
    }

    public static DisplayDetectionResult Read()
    {
        try
        {
            return new DisplayDetectionResult(WindowsDesktopWallpaper.GetMonitors(), null);
        }
        catch (Exception ex)
        {
            return new DisplayDetectionResult([], ex.Message);
        }
    }

    /// <summary>
    /// Stable panel identity: device name + geometry. Kept in the same format as
    /// ScheduleStore.BuildFingerprint so Displays and Auto rotation adopt the
    /// same physical panel after a driver/USB/GPU change gives it a new COM path.
    /// </summary>
    public static string Fingerprint(DisplayMonitor monitor) =>
        Fingerprint(monitor.DeviceName, monitor.Left, monitor.Top, monitor.Width, monitor.Height);

    public static string Fingerprint(string? deviceName, int left, int top, int width, int height) =>
        $"{(deviceName ?? string.Empty).Trim()}|{left},{top},{width}x{height}";

    private static void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var handlers = _displaysChanged;
        if (handlers is null)
            return;

        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                var snapshot = handlers;
                dispatcher.BeginInvoke(new Action(() => RaiseSafely(snapshot)));
                return;
            }

            RaiseSafely(handlers);
        }
        catch
        {
            // Windows must never see an exception from a display notification.
        }
    }

    private static void RaiseSafely(EventHandler handlers)
    {
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(null, EventArgs.Empty);
            }
            catch
            {
                // One faulty subscriber must not stop the others (or Windows).
            }
        }
    }
}
