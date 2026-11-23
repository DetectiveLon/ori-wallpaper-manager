using Oriole.Core.Models;

namespace Oriole.Core.Services;

/// <summary>
/// Pure geometry check used to decide whether a foreground window covers a display.
/// A small tolerance accounts for invisible Windows resize borders and rounding.
/// </summary>
public static class FullscreenWindowMatcher
{
    public const int DefaultTolerancePixels = 12;

    public static bool CoversDisplay(
        ScreenRectangle window,
        ScreenRectangle display,
        int tolerancePixels = DefaultTolerancePixels)
    {
        if (tolerancePixels < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerancePixels));

        if (!window.IsValid || !display.IsValid)
            return false;

        return window.Left <= display.Left + tolerancePixels
            && window.Top <= display.Top + tolerancePixels
            && window.Right >= display.Right - tolerancePixels
            && window.Bottom >= display.Bottom - tolerancePixels;
    }
}
