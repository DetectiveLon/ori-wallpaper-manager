using System.Runtime.InteropServices;
using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Desktop.Services;

/// <summary>
/// Checks whether the foreground window covers a specific display. This intentionally
/// detects any full-screen application, not games alone.
/// </summary>
public static class FullscreenWindowDetector
{
    public static bool IsFullscreenOnDisplay(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!OperatingSystem.IsWindows())
            return false;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero
            || window == GetShellWindow()
            || !IsWindowVisible(window)
            || IsIconic(window))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId)
            return false;

        if (!GetWindowRect(window, out var bounds))
            return false;

        var windowBounds = new ScreenRectangle(
            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var displayBounds = new ScreenRectangle(
            monitor.Left,
            monitor.Top,
            monitor.Left + monitor.Width,
            monitor.Top + monitor.Height);

        return FullscreenWindowMatcher.CoversDisplay(windowBounds, displayBounds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
