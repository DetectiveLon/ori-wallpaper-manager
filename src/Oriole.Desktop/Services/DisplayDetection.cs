namespace Oriole.Desktop.Services;

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
}
