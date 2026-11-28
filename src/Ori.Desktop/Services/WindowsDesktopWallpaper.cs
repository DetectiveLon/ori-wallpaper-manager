using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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
    string? CurrentWallpaper)
{
    /// <summary>
    /// Windows' own display number from \\.\DISPLAYn, not the COM enumeration
    /// index (which is volatile and can point at a stale instance path).
    /// </summary>
    public string DisplayNumberText
    {
        get
        {
            var match = Regex.Match(DeviceName ?? string.Empty, @"DISPLAY\s*(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? $"Display {match.Groups[1].Value}" : (DeviceName?.Trim() ?? "Display ?");
        }
    }

    /// <summary>Stable identity used to preserve selection and preferences across hotplug.</summary>
    public string Fingerprint => DisplayDetection.Fingerprint(this);
}

/// <summary>
/// Windows desktop integration using the documented IDesktopWallpaper COM API.
/// Keep this in the desktop project; the cross-platform core must not depend on COM.
/// All methods are synchronous and must be called from an STA thread (the WPF UI thread is STA).
/// </summary>
public static class WindowsDesktopWallpaper
{
    private const string DesktopWallpaperClassId = "C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD";

    /// <summary>
    /// The formats Windows can actually apply as a desktop wallpaper. The image
    /// cache/downloader uses the same allowlist so a GIF or WebP is skipped
    /// before it ever reaches this API.
    /// </summary>
    public static IReadOnlyList<string> SupportedImageExtensions { get; } = new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"
    };

    public static bool IsSupportedImageExtension(string extension) =>
        SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        EnsureWindows();
        var nativeMonitors = EnumerateNativeMonitors();

        return WithDesktopWallpaper(desktop =>
        {
            desktop.GetMonitorDevicePathCount(out uint count);
            var result = new List<DisplayMonitor>(checked((int)count));

            // A driver re-enumeration can leave a dead COM path next to the live
            // one. Both describe the same physical panel, so keep only the path
            // that actually responds to reads. The key uses the Win32 monitor
            // identity (not the COM index): two genuinely distinct panels that
            // happen to share bounds are never collapsed into one card.
            var bestByPanel = new Dictionary<(string Panel, int Left, int Top, int Right, int Bottom), MonitorCandidate>();
            var assignedNatives = new HashSet<int>();

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

                // Exact geometry first, preferring a Win32 monitor that has not
                // been claimed yet; a duplicate COM path may reuse the same one.
                var nativeIndex = FindNativeMonitor(nativeMonitors, bounds, assignedNatives);
                if (nativeIndex >= 0)
                    assignedNatives.Add(nativeIndex);

                var native = nativeIndex >= 0 ? nativeMonitors[nativeIndex] : null;
                // Never borrow a different panel's device name: an unmatched COM
                // path must be labelled honestly rather than showing a duplicate
                // Windows display number.
                var displayName = native?.DeviceName ?? $"UNKNOWN-{index + 1}";

                var monitor = new DisplayMonitor(
                    monitorId,
                    displayName,
                    bounds.Left,
                    bounds.Top,
                    width,
                    height,
                    native?.IsPrimary ?? nativeIndex == 0,
                    string.IsNullOrWhiteSpace(currentWallpaper) ? null : currentWallpaper);

                var panelKey = nativeIndex >= 0
                    ? $"win32:{nativeIndex}"
                    : $"com:{monitorId}";
                var panel = (panelKey, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
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
            // Span is a global fit mode set by ApplySpanWallpaper. Windows then
            // ignores per-monitor images, and no per-monitor caller passes a
            // position, so the old Span would leak into every later apply. Reset
            // it to Fill before setting this monitor's image. Other fit modes
            // are left alone; forcing Fill would crop wallpapers chosen elsewhere.
            try
            {
                desktop.GetPosition(out var current);
                if (current == DesktopWallpaperPosition.Span)
                    desktop.SetPosition(DesktopWallpaperPosition.Fill);
            }
            catch (COMException)
            {
                // Shells without GetPosition still accept SetWallpaper; the
                // explicit position below (if any) is applied as requested.
            }

            if (position is { } requested)
                desktop.SetPosition(requested);
            desktop.SetWallpaper(monitorId, fullPath);
            return true;
        });
    }

    /// <summary>
    /// Validates a local wallpaper file against the format allowlist. Public so
    /// callers and tests can check a path before applying it.
    /// </summary>
    public static string ValidateWallpaperPath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected wallpaper file does not exist.", fullPath);

        if (!IsSupportedImageExtension(Path.GetExtension(fullPath)))
            throw new InvalidDataException(
                "Skipped: unsupported image format. Choose a JPG, PNG, BMP or TIFF image; GIF and WebP cannot be applied to the desktop.");

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

    /// <summary>
    /// Finds the Win32 monitor that owns a COM path's bounds, preferring an
    /// unclaimed monitor so two genuinely distinct panels never share identity.
    /// Returns -1 when Windows cannot match the path to a live monitor.
    /// </summary>
    private static int FindNativeMonitor(
        IReadOnlyList<NativeMonitor> monitors,
        NativeRect bounds,
        HashSet<int> assigned)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = 0; index < monitors.Count; index++)
            {
                var candidate = monitors[index];
                if (pass == 0 && assigned.Contains(index))
                    continue;

                if (candidate.Bounds.Left == bounds.Left
                    && candidate.Bounds.Top == bounds.Top
                    && candidate.Bounds.Right == bounds.Right
                    && candidate.Bounds.Bottom == bounds.Bottom)
                {
                    return index;
                }
            }
        }

        // DPI or driver changes can shift a path's rect by a pixel. Claim the
        // closest unused monitor by centre distance, but only when it overlaps.
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        var centerX = (bounds.Left + bounds.Right) / 2d;
        var centerY = (bounds.Top + bounds.Bottom) / 2d;
        for (var index = 0; index < monitors.Count; index++)
        {
            if (assigned.Contains(index))
                continue;

            var rect = monitors[index].Bounds;
            if (rect.Right <= bounds.Left || rect.Left >= bounds.Right
                || rect.Bottom <= bounds.Top || rect.Top >= bounds.Bottom)
            {
                continue;
            }

            var distance = Math.Abs((rect.Left + rect.Right) / 2d - centerX)
                + Math.Abs((rect.Top + rect.Bottom) / 2d - centerY);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
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
                    monitors.Count,
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

    private sealed record NativeMonitor(int Index, string DeviceName, NativeRect Bounds, bool IsPrimary);

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
