using System.Diagnostics;
using System.Runtime.InteropServices;
using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Desktop.Services;

/// <summary>
/// Detects full-screen windows covering a specific display. This intentionally
/// detects any full-screen application, not games alone.
/// </summary>
/// <remarks>
/// The original implementation only inspected the foreground window and returned
/// false whenever Ori itself was foreground. That meant alt-tabbing to Ori
/// while a game was still full-screen cleared the pause. This version enumerates
/// top-level windows and remembers full-screen windows by handle/PID across focus
/// changes, so focusing Ori no longer masks a full-screen app. The original
/// public method keeps its signature and callers keep working unchanged.
/// </remarks>
public static class FullscreenWindowDetector
{
    private static readonly object Gate = new();

    /// <summary>Last-known full-screen windows, keyed by their HWND.</summary>
    private static readonly Dictionary<IntPtr, FullscreenWindow> Known = new();

    /// <summary>Process-name cache to avoid repeated Process lookups on every tick.</summary>
    private static readonly Dictionary<int, string> ProcessNames = new();

    /// <summary>
    /// Process names that must never count as a full-screen blocker. Callers may
    /// extend this (e.g. a dedicated kiosk/overlay) without changing the detector.
    /// Matching is case-insensitive.
    /// </summary>
    public static ISet<string> ExemptProcessNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Friendly process name of the window that most recently blocked rotation,
    /// or null when nothing is blocking. Useful for status text / diagnostics.
    /// </summary>
    public static string? BlockingAppName
    {
        get { lock (Gate) return _blockingAppName; }
    }

    private static string? _blockingAppName;

    /// <summary>
    /// Adds a process name to the exemption list (convenience wrapper around
    /// <see cref="ExemptProcessNames"/>). Null/blank names are ignored.
    /// </summary>
    public static void AddExemptProcess(string? processName)
    {
        if (!string.IsNullOrWhiteSpace(processName))
        {
            lock (Gate)
                ExemptProcessNames.Add(processName.Trim());
        }
    }

    /// <summary>Original API. Returns true when a full-screen window covers the display.</summary>
    public static bool IsFullscreenOnDisplay(DisplayMonitor monitor) =>
        IsFullscreenOnDisplay(monitor, out _);

    /// <summary>
    /// Same check as <see cref="IsFullscreenOnDisplay(DisplayMonitor)"/>, but also
    /// reports the name of the blocking application (null when nothing blocks).
    /// </summary>
    public static bool IsFullscreenOnDisplay(DisplayMonitor monitor, out string? blockingAppName)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        blockingAppName = null;
        if (!OperatingSystem.IsWindows())
            return false;

        var displayBounds = new ScreenRectangle(
            monitor.Left,
            monitor.Top,
            monitor.Left + monitor.Width,
            monitor.Top + monitor.Height);
        if (!displayBounds.IsValid)
            return false;

        var ownPid = Environment.ProcessId;
        var foreground = GetForegroundWindow();
        var foregroundPid = GetPid(foreground);

        // A focus change to one of our own windows must not be read as "the
        // full-screen app went away": keep the remembered full-screen state.
        var focusIsOurs = foreground != IntPtr.Zero && foregroundPid == ownPid;

        var current = new List<FullscreenWindow>();
        var shellWindow = GetShellWindow();

        lock (Gate)
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (hwnd == IntPtr.Zero || hwnd == shellWindow || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                        return true;

                    var pid = GetPid(hwnd);
                    if (pid == 0 || pid == ownPid)
                        return true;

                    var name = GetProcessName((int)pid);
                    if (name is not null && ExemptProcessNames.Contains(name))
                        return true;

                    if (!GetWindowRect(hwnd, out var bounds))
                        return true;

                    var windowBounds = new ScreenRectangle(
                        bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                    if (!FullscreenWindowMatcher.CoversDisplay(windowBounds, displayBounds))
                        return true;

                    current.Add(new FullscreenWindow(hwnd, pid, name ?? $"PID {pid}", windowBounds));
                }
                catch
                {
                    // A single bad window must never abort enumeration.
                }
                return true;
            }, IntPtr.Zero);

            FullscreenWindow? blocker;

            if (focusIsOurs)
            {
                // Retain remembered windows that still exist and are not exempt,
                // even if they are temporarily minimised by the alt-tab.
                foreach (var entry in Known.Values.ToArray())
                {
                    if (!IsWindow(entry.Handle) || ExemptProcessNames.Contains(entry.Name))
                        Known.Remove(entry.Handle);
                }
                foreach (var window in current)
                    Known[window.Handle] = window;

                blocker = current.FirstOrDefault()
                          ?? Known.Values.FirstOrDefault(entry => Covers(entry.Bounds, displayBounds));
            }
            else
            {
                // Focus belongs to another app (or the shell): trust the live
                // enumeration and drop anything that is no longer full-screen.
                Known.Clear();
                foreach (var window in current)
                    Known[window.Handle] = window;
                blocker = current.FirstOrDefault();
            }

            blockingAppName = blocker?.Name;
            _blockingAppName = blockingAppName;
            return blocker is not null;
        }
    }

    private static bool Covers(ScreenRectangle window, ScreenRectangle display) =>
        FullscreenWindowMatcher.CoversDisplay(window, display);

    private static uint GetPid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return 0;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string? GetProcessName(int pid)
    {
        if (pid <= 0)
            return null;
        lock (Gate)
        {
            if (ProcessNames.TryGetValue(pid, out var cached))
                return cached;
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            // Process may have exited, be protected, or be inaccessible.
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            lock (Gate)
                ProcessNames[pid] = name;
        }
        return name;
    }

    private sealed record FullscreenWindow(
        IntPtr Handle, uint Pid, string Name, ScreenRectangle Bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
}
