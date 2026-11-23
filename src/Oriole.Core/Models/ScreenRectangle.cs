namespace Oriole.Core.Models;

/// <summary>
/// Rectangle in desktop screen coordinates. Right and Bottom are exclusive edges,
/// matching the Win32 RECT convention.
/// </summary>
public readonly record struct ScreenRectangle(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Right > Left && Bottom > Top;

    public int Width => IsValid ? Right - Left : 0;

    public int Height => IsValid ? Bottom - Top : 0;
}
