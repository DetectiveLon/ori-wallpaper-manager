using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Oriole.Desktop.Services;

public enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5
}

public sealed record DisplayMonitor(
    string Id,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    string? CurrentWallpaper);

/// <summary>
/// Windows desktop integration using the documented IDesktopWallpaper COM API.
/// Keep this in the desktop project; the cross-platform core must not depend on COM.
/// All methods are synchronous and must be called from an STA thread (the WPF UI thread is STA).
/// </summary>
public static class WindowsDesktopWallpaper
{
    private const string DesktopWallpaperClassId = "C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD";

    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        EnsureWindows();
        var nativeMonitors = EnumerateNativeMonitors();

        return WithDesktopWallpaper(desktop =>
        {
            desktop.GetMonitorDevicePathCount(out uint count);
            var result = new List<DisplayMonitor>(checked((int)count));

            for (uint index = 0; index < count; index++)
            {
                desktop.GetMonitorDevicePathAt(index, out string monitorId);
                desktop.GetMonitorRECT(monitorId, out NativeRect bounds);

                int width = bounds.Right - bounds.Left;
                int height = bounds.Bottom - bounds.Top;
                if (width <= 0 || height <= 0)
                    continue; // GetMonitorDevicePathAt can also return detached monitors.

                desktop.GetWallpaper(monitorId, out string? currentWallpaper);

                var native = nativeMonitors.FirstOrDefault(candidate =>
                    candidate.Bounds.Left == bounds.Left
                    && candidate.Bounds.Top == bounds.Top
                    && candidate.Bounds.Right == bounds.Right
                    && candidate.Bounds.Bottom == bounds.Bottom);

                result.Add(new DisplayMonitor(
                    monitorId,
                    native?.DeviceName ?? $"DISPLAY{index + 1}",
                    bounds.Left,
                    bounds.Top,
                    width,
                    height,
                    native?.IsPrimary ?? index == 0,
                    string.IsNullOrWhiteSpace(currentWallpaper) ? null : currentWallpaper));
            }

            return (IReadOnlyList<DisplayMonitor>)result;
        });
    }

    public static void ApplyWallpaper(
        string monitorId,
        string imagePath,
        DesktopWallpaperPosition position = DesktopWallpaperPosition.Fill)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected wallpaper file does not exist.", fullPath);

        var extension = Path.GetExtension(fullPath);
        if (!new[] { ".jpg", ".jpeg", ".png", ".bmp", ".dib", ".tif", ".tiff" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a JPG, PNG, BMP, or TIFF image.");
        }

        WithDesktopWallpaper(desktop =>
        {
            // Windows exposes the desktop positioning mode globally. Image assignment
            // remains per-monitor; independent scaling modes will need image composition.
            desktop.SetPosition(position);
            desktop.SetWallpaper(monitorId, fullPath);
            return true;
        });
    }

    private static T WithDesktopWallpaper<T>(Func<IDesktopWallpaper, T> action)
    {
        object instance = new DesktopWallpaperClass();
        try
        {
            return action((IDesktopWallpaper)instance);
        }
        finally
        {
            if (Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
    }

    private static List<NativeMonitor> EnumerateNativeMonitors()
    {
        var monitors = new List<NativeMonitor>();
        MonitorEnumProc callback = (IntPtr handle, IntPtr deviceContext, ref NativeRect monitorRect, IntPtr userData) =>
        {
            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty
            };

            if (GetMonitorInfo(handle, ref info))
            {
                monitors.Add(new NativeMonitor(
                    info.DeviceName,
                    info.Monitor,
                    (info.Flags & 1) != 0));
            }

            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enumerate connected displays.");

        GC.KeepAlive(callback);
        return monitors;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Desktop wallpaper control is available only on Windows.");
    }

    private sealed record NativeMonitor(string DeviceName, NativeRect Bounds, bool IsPrimary);

    [ComImport]
    [Guid(DesktopWallpaperClassId)]
    private sealed class DesktopWallpaperClass
    {
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        void GetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] out string? wallpaper);

        void GetMonitorDevicePathAt(
            uint monitorIndex,
            [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);

        void GetMonitorDevicePathCount(out uint count);

        void GetMonitorRECT(
            [MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            out NativeRect displayRect);

        void SetBackgroundColor(uint color);

        void GetBackgroundColor(out uint color);

        void SetPosition(DesktopWallpaperPosition position);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(
        IntPtr monitor,
        IntPtr deviceContext,
        ref NativeRect monitorRect,
        IntPtr userData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProc callback,
        IntPtr userData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
