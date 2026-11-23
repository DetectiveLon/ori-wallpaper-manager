using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

public sealed class FullscreenWindowMatcherTests
{
    private static readonly ScreenRectangle Display = new(0, 0, 1920, 1080);

    [Fact]
    public void ExactDisplayBoundsAreFullscreen()
    {
        Assert.True(FullscreenWindowMatcher.CoversDisplay(Display, Display));
    }

    [Fact]
    public void SmallWindowFrameRoundingDifferencesAreTolerated()
    {
        var window = new ScreenRectangle(-4, 3, 1916, 1076);

        Assert.True(FullscreenWindowMatcher.CoversDisplay(window, Display));
    }

    [Fact]
    public void WindowLeavingTaskbarAreaIsNotFullscreen()
    {
        var maximizedWindow = new ScreenRectangle(0, 0, 1920, 1040);

        Assert.False(FullscreenWindowMatcher.CoversDisplay(maximizedWindow, Display));
    }

    [Fact]
    public void WindowThatDoesNotCoverEveryDisplayEdgeIsNotFullscreen()
    {
        var partialWindow = new ScreenRectangle(100, 0, 1920, 1080);

        Assert.False(FullscreenWindowMatcher.CoversDisplay(partialWindow, Display));
    }

    [Fact]
    public void SpanningWindowCanCoverEachDisplayIndependently()
    {
        var leftDisplay = new ScreenRectangle(0, 0, 1920, 1080);
        var rightDisplay = new ScreenRectangle(1920, 0, 3840, 1080);
        var spanningWindow = new ScreenRectangle(0, 0, 3840, 1080);

        Assert.True(FullscreenWindowMatcher.CoversDisplay(spanningWindow, leftDisplay));
        Assert.True(FullscreenWindowMatcher.CoversDisplay(spanningWindow, rightDisplay));
    }

    [Fact]
    public void InvalidRectanglesDoNotMatch()
    {
        var invalidWindow = new ScreenRectangle(20, 10, 10, 100);

        Assert.False(FullscreenWindowMatcher.CoversDisplay(invalidWindow, Display));
    }

    [Fact]
    public void NegativeToleranceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FullscreenWindowMatcher.CoversDisplay(Display, Display, tolerancePixels: -1));
    }
}
