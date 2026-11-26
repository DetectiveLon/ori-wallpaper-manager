using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Core.Tests;

[Collection("FeedStore")]
public sealed class RotationFeedSelectionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path;

    public RotationFeedSelectionTests()
    {
        _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ori-selection-" + Guid.NewGuid().ToString("N") + ".json");
        WallhavenFeedStore.SetPathForTests(_path);
    }

    public void Dispose()
    {
        WallhavenFeedStore.ResetPathForTests();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void SelectNextFeedStartsAtFirstFeed()
    {
        var feeds = new[] { Feed("a"), Feed("b"), Feed("c") };

        Assert.Equal("a", WallhavenFeedStore.SelectNextFeed(feeds, T0)!.Id);
    }

    [Fact]
    public void SelectNextFeedRotatesRoundRobinAfterSuccess()
    {
        var feeds = new[]
        {
            Feed("a") with { CadenceMinutes = 1 },
            Feed("b") with { CadenceMinutes = 1 },
            Feed("c") with { CadenceMinutes = 1 }
        };

        var first = WallhavenFeedStore.SelectNextFeed(feeds, T0);
        Assert.Equal("a", first!.Id);
        feeds[0] = first with { LastFetchedUtc = T0 };

        var second = WallhavenFeedStore.SelectNextFeed(feeds, T0.AddMinutes(1));
        Assert.Equal("b", second!.Id);
        feeds[1] = second with { LastFetchedUtc = T0.AddMinutes(1) };

        var third = WallhavenFeedStore.SelectNextFeed(feeds, T0.AddMinutes(2));
        Assert.Equal("c", third!.Id);
        feeds[2] = third with { LastFetchedUtc = T0.AddMinutes(2) };

        Assert.Equal("a", WallhavenFeedStore.SelectNextFeed(feeds, T0.AddMinutes(3))!.Id);
    }

    [Fact]
    public void SelectNextFeedSkipsDisabledFeeds()
    {
        var feeds = new[] { Feed("a") with { Enabled = false }, Feed("b"), Feed("c") };

        Assert.Equal("b", WallhavenFeedStore.SelectNextFeed(feeds, T0)!.Id);

        Assert.Null(WallhavenFeedStore.SelectNextFeed(
            new[] { feeds[0], feeds[1] with { Enabled = false } }, T0));
    }

    [Fact]
    public void SelectNextFeedWaitsForCadence()
    {
        var feeds = new[] { Feed("a") with { LastFetchedUtc = T0, CadenceMinutes = 60 } };

        Assert.Null(WallhavenFeedStore.SelectNextFeed(feeds, T0.AddMinutes(59)));
        Assert.Equal("a", WallhavenFeedStore.SelectNextFeed(feeds, T0.AddMinutes(60))!.Id);
        Assert.Equal("a", WallhavenFeedStore.SelectNextFeed(
            feeds, T0.AddMinutes(59), respectCadence: false)!.Id);
    }

    [Fact]
    public void SelectNextFeedWaitsForBackoff()
    {
        var feeds = new[]
        {
            Feed("a") with
            {
                LastFetchedUtc = T0.AddHours(-2),
                BackoffUntilUtc = T0.AddSeconds(30)
            }
        };

        Assert.Null(WallhavenFeedStore.SelectNextFeed(feeds, T0.AddSeconds(10)));
        Assert.Equal("a", WallhavenFeedStore.SelectNextFeed(feeds, T0.AddSeconds(31))!.Id);
        Assert.Equal("a", WallhavenFeedStore.SelectNextFeed(
            feeds, T0.AddSeconds(10), respectBackoff: false)!.Id);
    }

    [Fact]
    public void SelectNextFeedIsPure()
    {
        var feeds = new[]
        {
            Feed("a") with { LastFetchedUtc = T0.AddMinutes(-10) },
            Feed("b") with { BackoffUntilUtc = T0.AddSeconds(-30) }
        };
        var snapshot = feeds.ToArray();

        var selected = WallhavenFeedStore.SelectNextFeed(feeds, T0);
        WallhavenFeedStore.SelectNextFeed(feeds, T0, respectCadence: false, respectBackoff: false);
        WallhavenFeedStore.SelectNextFeed(Array.Empty<RotationFeed>(), T0);

        Assert.Same(feeds[1], selected);
        Assert.Equal(2, feeds.Length);
        Assert.Same(snapshot[0], feeds[0]);
        Assert.Same(snapshot[1], feeds[1]);
        Assert.Equal(snapshot[0], feeds[0]);
        Assert.Equal(snapshot[1], feeds[1]);
        Assert.Equal("b", WallhavenFeedStore.SelectNextFeed(feeds, T0)!.Id);
    }

    private static RotationFeed Feed(string id) => new()
    {
        Id = id,
        Name = id,
        Filters = new WallhavenFilters { Query = id },
        CadenceMinutes = 60
    };
}
