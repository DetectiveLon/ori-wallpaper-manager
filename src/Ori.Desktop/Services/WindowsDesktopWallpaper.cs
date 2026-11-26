using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Ori.Desktop.Services;

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

            // A driver re-enumeration can leave a dead COM path next to the live
            // one. Both describe the same physical panel (same device name and
            // bounds), so keep only the path that actually responds to reads.
            var bestByPanel = new Dictionary<(string Name, int Left, int Top, int Right, int Bottom), MonitorCandidate>();

            for (uint index = 0; index < count; index++)
            {
                string monitorId;
                NativeRect bounds;
                try
                {
                    desktop.GetMonitorDevicePathAt(index, out monitorId);
                    // A stale path fails here with E_FAIL. It cannot apply a
                    // wallpaper, so it must never produce a duplicate card.
                    desktop.GetMonitorRECT(monitorId, out bounds);
                }
                catch (COMException)
                {
                    continue;
                }

                int width = bounds.Right - bounds.Left;
                int height = bounds.Bottom - bounds.Top;
                if (width <= 0 || height <= 0)
                    continue; // Detached monitors have no usable geometry.

                string? currentWallpaper = null;
                var wallpaperReadSucceeded = false;
                try
                {
                    desktop.GetWallpaper(monitorId, out currentWallpaper);
                    wallpaperReadSucceeded = true;
                }
                catch (COMException)
                {
                    // Geometry still identifies the panel; only the ranking of
                    // duplicate paths is affected by a failed wallpaper read.
                    currentWallpaper = null;
                }

                var native = nativeMonitors.FirstOrDefault(candidate =>
                    candidate.Bounds.Left == bounds.Left
                    && candidate.Bounds.Top == bounds.Top
                    && candidate.Bounds.Right == bounds.Right
                    && candidate.Bounds.Bottom == bounds.Bottom);

                var monitor = new DisplayMonitor(
                    monitorId,
                    native?.DeviceName ?? $"DISPLAY{index + 1}",
                    bounds.Left,
                    bounds.Top,
                    width,
                    height,
                    native?.IsPrimary ?? index == 0,
                    string.IsNullOrWhiteSpace(currentWallpaper) ? null : currentWallpaper);

                var panel = (monitor.DeviceName, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                var candidate = new MonitorCandidate(monitor, wallpaperReadSucceeded, checked((int)index));
                if (bestByPanel.TryGetValue(panel, out var existing))
                {
                    // Prefer the path whose wallpaper read succeeded; if both
                    // work, keep the first path Windows reported.
                    if ((candidate.WallpaperReadSucceeded && !existing.WallpaperReadSucceeded)
                        || (candidate.WallpaperReadSucceeded == existing.WallpaperReadSucceeded
                            && candidate.Index < existing.Index))
                    {
                        bestByPanel[panel] = candidate;
                    }
                }
                else
                {
                    bestByPanel.Add(panel, candidate);
                }
            }

            result.AddRange(bestByPanel.Values
                .OrderBy(candidate => candidate.Index)
                .Select(candidate => candidate.Monitor));

            // If geometry matching failed, the index fallback can mark a second
            // monitor primary. Keep exactly one PRIMARY card.
            var primary = result.FirstOrDefault(monitor => monitor.IsPrimary);
            if (primary is null)
            {
                if (result.Count > 0)
                    result[0] = result[0] with { IsPrimary = true };
            }
            else
            {
                for (var index = 0; index < result.Count; index++)
                {
                    if (!ReferenceEquals(result[index], primary) && result[index].IsPrimary)
                        result[index] = result[index] with { IsPrimary = false };
                }
            }

            return result;
        });
    }

    /// <summary>Reads the Windows-wide fit mode; Fill is assumed when Windows cannot report one.</summary>
    public static DesktopWallpaperPosition GetPosition()
    {
        EnsureWindows();
        try
        {
            return WithDesktopWallpaper(desktop =>
            {
                desktop.GetPosition(out var position);
                return Enum.IsDefined(position) ? position : DesktopWallpaperPosition.Fill;
            });
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            // Virtualised and legacy shells may not implement GetPosition. The fit
            // control stays usable with an honest "assumed Fill" fallback.
            return DesktopWallpaperPosition.Fill;
        }
    }

    /// <summary>Sets the single fit mode Windows applies to every display.</summary>
    public static void SetPosition(DesktopWallpaperPosition position)
    {
        EnsureWindows();
        WithDesktopWallpaper(desktop =>
        {
            desktop.SetPosition(position);
            return true;
        });
    }

    /// <summary>
    /// Applies one image to the whole virtual desktop. A null monitor id is the
    /// documented "all monitors" target and is what Span mode requires.
    /// </summary>
    public static void ApplySpanWallpaper(string imagePath)
    {
        EnsureWindows();
        var fullPath = ValidateWallpaperPath(imagePath);
        WithDesktopWallpaper(desktop =>
        {
            // Span is a global fit mode too: set it before targeting all monitors,
            // otherwise the OS would keep the previous fit while Ori reports Span.
            desktop.SetPosition(DesktopWallpaperPosition.Span);
            desktop.SetWallpaper(null, fullPath);
            return true;
        });
    }

    public static void ApplyWallpaper(
        string monitorId,
        string imagePath,
        DesktopWallpaperPosition? position = null)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        var fullPath = ValidateWallpaperPath(imagePath);

        WithDesktopWallpaper(desktop =>
        {
            // The OS fit mode is global. Leave it alone unless the caller explicitly
            // asked to change it; forcing Fill here crops wallpapers chosen elsewhere.
            if (position is { } requested)
                desktop.SetPosition(requested);
            desktop.SetWallpaper(monitorId, fullPath);
            return true;
        });
    }

    private static string ValidateWallpaperPath(string imagePath)
    {
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

        return fullPath;
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

    private sealed record MonitorCandidate(DisplayMonitor Monitor, bool WallpaperReadSucceeded, int Index);

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

        void GetPosition(out DesktopWallpaperPosition position);
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
