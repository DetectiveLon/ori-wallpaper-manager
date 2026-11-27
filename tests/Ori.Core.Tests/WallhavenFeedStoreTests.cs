using System.Text.Json;
using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Core.Tests;

/// <summary>The path override is process-wide static state, so all feed-store tests share one serial collection.</summary>
[CollectionDefinition("FeedStore", DisableParallelization = true)]
public sealed class FeedStoreCollection
{
}

[Collection("FeedStore")]
public sealed class WallhavenFeedStoreTests : IDisposable
{
    private readonly string _path;
    private readonly string _directory;
    private readonly string _fileName;

    public WallhavenFeedStoreTests()
    {
        _fileName = "ori-feed-" + Guid.NewGuid().ToString("N") + ".json";
        _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), _fileName);
        _directory = System.IO.Path.GetDirectoryName(_path)!;
        WallhavenFeedStore.SetPathForTests(_path);
    }

    public void Dispose()
    {
        WallhavenFeedStore.ResetPathForTests();
        foreach (var backup in Directory.GetFiles(_directory, _fileName + ".corrupt-*"))
            File.Delete(backup);
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void V1RecordMigratesToOneFeedNamedSavedSearch()
    {
        WriteRaw(V1Json("nature", 120, autoFetchEnabled: true));

        var feed = Assert.Single(WallhavenFeedStore.ListFeeds());

        Assert.Equal(WallhavenFeedStore.MigratedFeedId, feed.Id);
        Assert.Equal(WallhavenFeedStore.MigratedFeedName, feed.Name);
        Assert.Equal(120, feed.CadenceMinutes);
        Assert.Equal("nature", feed.Filters.Query);
        Assert.True(feed.Enabled);
        Assert.Null(feed.LastFetchedUtc);
        Assert.Null(feed.BackoffUntilUtc);
        Assert.Equal(0, feed.ConsecutiveFailures);
        Assert.Null(feed.LastAppliedId);
        Assert.True(WallhavenFeedStore.IsAutoFetchEnabled());
    }

    [Fact]
    public void V1WithoutFiltersKeepsMasterFlagAndYieldsNoFeeds()
    {
        WriteRaw(V1Json(null, 60, autoFetchEnabled: true));

        Assert.Empty(WallhavenFeedStore.ListFeeds());
        Assert.Null(WallhavenFeedStore.Load());
        Assert.True(WallhavenFeedStore.IsAutoFetchEnabled());
    }

    [Fact]
    public void V1ZeroCadenceFallsBackToSixtyMinutes()
    {
        WriteRaw(V1Json("nature", 0, autoFetchEnabled: false));

        var feed = Assert.Single(WallhavenFeedStore.ListFeeds());

        Assert.Equal(WallhavenFeedStore.DefaultCadenceMinutes, feed.CadenceMinutes);
        Assert.False(WallhavenFeedStore.IsAutoFetchEnabled());
    }

    [Fact]
    public void V1MigrationSurvivesReloadAndPromotesVersion()
    {
        WriteRaw(V1Json("nature", 45, autoFetchEnabled: true));
        Assert.Single(WallhavenFeedStore.ListFeeds());

        // Master flag is already true, so this is a no-op change: only the pending
        // migration should force the write.
        WallhavenFeedStore.SetAutoFetchEnabled(true);

        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        var feeds = document.RootElement.GetProperty("Feeds");
        Assert.Equal(1, feeds.GetArrayLength());
        Assert.Equal("saved-search", feeds[0].GetProperty("Id").GetString());
        Assert.True(document.RootElement.GetProperty("AutoFetchEnabled").GetBoolean());

        var reloaded = Assert.Single(WallhavenFeedStore.ListFeeds());
        Assert.Equal("nature", reloaded.Filters.Query);
        Assert.Equal(45, reloaded.CadenceMinutes);
    }

    [Fact]
    public void LoadWithCadenceStillReadsMigratedFeed()
    {
        WriteRaw(V1Json("nature", 90, autoFetchEnabled: false));

        var loaded = WallhavenFeedStore.LoadWithCadence();

        Assert.NotNull(loaded);
        Assert.Equal("nature", loaded.Value.Filters.Query);
        Assert.Equal(90, loaded.Value.CadenceMinutes);
        Assert.Equal("nature", WallhavenFeedStore.Load()!.Query);
    }

    [Fact]
    public void LegacySaveUpdatesOnlyMigratedFeed()
    {
        WallhavenFeedStore.SetAutoFetchEnabled(true);
        WallhavenFeedStore.UpsertFeed(new RotationFeed
        {
            Id = "user-feed", Name = "User", Filters = new WallhavenFilters { Query = "cat" }, CadenceMinutes = 30
        });
        WallhavenFeedStore.UpsertFeed(new RotationFeed
        {
            Id = WallhavenFeedStore.MigratedFeedId,
            Name = WallhavenFeedStore.MigratedFeedName,
            Filters = new WallhavenFilters { Query = "old" },
            CadenceMinutes = 10
        });

        WallhavenFeedStore.Save(new WallhavenFilters { Query = "legacy" }, 45);

        var feeds = WallhavenFeedStore.ListFeeds();
        Assert.Equal(2, feeds.Count);
        Assert.Equal("user-feed", feeds[0].Id);
        Assert.Equal("cat", feeds[0].Filters.Query);
        Assert.Equal(30, feeds[0].CadenceMinutes);
        Assert.Equal(WallhavenFeedStore.MigratedFeedId, feeds[1].Id);
        Assert.Equal("legacy", feeds[1].Filters.Query);
        Assert.Equal(45, feeds[1].CadenceMinutes);
        Assert.True(feeds[1].Enabled);
        Assert.True(WallhavenFeedStore.IsAutoFetchEnabled());
    }

    [Fact]
    public void UpsertFeedRoundTripsAllFields()
    {
        var fetched = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var feed = new RotationFeed
        {
            Id = "alpha_1",
            Name = "Alpha",
            Filters = new WallhavenFilters { Query = "sky" },
            CadenceMinutes = 17,
            Enabled = false,
            LastFetchedUtc = fetched.AddMinutes(-5),
            BackoffUntilUtc = fetched.AddMinutes(3),
            ConsecutiveFailures = 4,
            LastAppliedId = "wall-123"
        };

        WallhavenFeedStore.UpsertFeed(feed);
        var stored = Assert.Single(WallhavenFeedStore.ListFeeds());

        Assert.Equal("alpha_1", stored.Id);
        Assert.Equal("Alpha", stored.Name);
        Assert.Equal("sky", stored.Filters.Query);
        Assert.Equal(17, stored.CadenceMinutes);
        Assert.False(stored.Enabled);
        Assert.Equal(fetched.AddMinutes(-5), stored.LastFetchedUtc);
        Assert.Equal(fetched.AddMinutes(3), stored.BackoffUntilUtc);
        Assert.Equal(4, stored.ConsecutiveFailures);
        Assert.Equal("wall-123", stored.LastAppliedId);
    }

    [Fact]
    public void UpsertFeedWithExistingIdReplacesInPlace()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a", "First"));
        WallhavenFeedStore.UpsertFeed(Feed("b", "Second"));

        WallhavenFeedStore.UpsertFeed(Feed("A", "Replaced"));

        var feeds = WallhavenFeedStore.ListFeeds();
        Assert.Equal(2, feeds.Count);
        Assert.Equal("A", feeds[0].Id);
        Assert.Equal("Replaced", feeds[0].Name);
        Assert.Equal("b", feeds[1].Id);
        Assert.Equal("Second", feeds[1].Name);
    }

    [Fact]
    public void RemoveFeedPreservesOrder()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a", "A"));
        WallhavenFeedStore.UpsertFeed(Feed("b", "B"));
        WallhavenFeedStore.UpsertFeed(Feed("c", "C"));

        WallhavenFeedStore.RemoveFeed("b");
        Assert.Equal(new[] { "a", "c" }, WallhavenFeedStore.ListFeeds().Select(f => f.Id));

        WallhavenFeedStore.RemoveFeed("missing");
        WallhavenFeedStore.RemoveFeed("  ");
        Assert.Equal(new[] { "a", "c" }, WallhavenFeedStore.ListFeeds().Select(f => f.Id));
    }

    [Fact]
    public void MoveFeedReordersAndClamps()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a", "A"));
        WallhavenFeedStore.UpsertFeed(Feed("b", "B"));
        WallhavenFeedStore.UpsertFeed(Feed("c", "C"));

        WallhavenFeedStore.MoveFeed("c", 0);
        Assert.Equal(new[] { "c", "a", "b" }, Ids());

        WallhavenFeedStore.MoveFeed("a", 999);
        Assert.Equal(new[] { "c", "b", "a" }, Ids());

        WallhavenFeedStore.MoveFeed("a", 1);
        Assert.Equal(new[] { "c", "a", "b" }, Ids());

        WallhavenFeedStore.MoveFeed("missing", 0);
        WallhavenFeedStore.MoveFeed("c", 0);
        Assert.Equal(new[] { "c", "a", "b" }, Ids());
    }

    [Fact]
    public void ListFeedsReturnsDefensiveCopy()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a", "A"));
        WallhavenFeedStore.UpsertFeed(Feed("b", "B"));

        var copy = Assert.IsType<RotationFeed[]>(WallhavenFeedStore.ListFeeds());
        copy[0] = Feed("replaced", "Replaced");
        copy[1] = null!;

        Assert.Equal(new[] { "a", "b" }, Ids());
    }

    [Fact]
    public void RejectsInvalidFeed()
    {
        var valid = Feed("good-id", "Good");
        var invalid = new[]
        {
            valid with { Id = " " },
            valid with { Id = "bad id!" },
            valid with { Id = new string('a', 65) },
            valid with { Name = " " },
            valid with { Name = new string('n', 81) },
            valid with { Filters = null! },
            valid with { Filters = new WallhavenFilters { Categories = WallhavenCategory.None } },
            valid with { CadenceMinutes = 0 },
            valid with { CadenceMinutes = 10081 },
            valid with { ConsecutiveFailures = -1 },
            valid with { ConsecutiveFailures = 21 },
            valid with { LastAppliedId = new string('x', 41) },
            valid with { LastAppliedId = "bad/id" }
        };

        foreach (var feed in invalid)
            Assert.ThrowsAny<ArgumentException>(() => feed.Validate());

        Assert.Throws<ArgumentException>(() => WallhavenFeedStore.UpsertFeed(invalid[0]));
        Assert.Throws<ArgumentNullException>(() => WallhavenFeedStore.UpsertFeed(null!));
        Assert.Empty(WallhavenFeedStore.ListFeeds());
    }

    [Fact]
    public void FeedsAreCappedAtTwenty()
    {
        for (var i = 0; i < WallhavenFeedStore.MaximumFeeds; i++)
            WallhavenFeedStore.UpsertFeed(Feed("feed-" + i, "Feed " + i));

        var overflow = Feed("feed-overflow", "Overflow");
        Assert.Throws<InvalidOperationException>(() => WallhavenFeedStore.UpsertFeed(overflow));

        Assert.Equal(WallhavenFeedStore.MaximumFeeds, WallhavenFeedStore.ListFeeds().Count);
    }

    [Fact]
    public void RecordSuccessClearsFailureState()
    {
        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        WallhavenFeedStore.UpsertFeed(new RotationFeed
        {
            Id = "a",
            Name = "A",
            Filters = new WallhavenFilters(),
            LastFetchedUtc = t0,
            BackoffUntilUtc = t0.AddMinutes(5),
            ConsecutiveFailures = 3,
            LastAppliedId = "old-id"
        });

        WallhavenFeedStore.RecordFetchResult("a", success: true, now: t0.AddMinutes(2), appliedId: "new-id");

        var stored = Assert.Single(WallhavenFeedStore.ListFeeds());
        Assert.Equal(t0.AddMinutes(2), stored.LastFetchedUtc);
        Assert.Null(stored.BackoffUntilUtc);
        Assert.Equal(0, stored.ConsecutiveFailures);
        Assert.Equal("new-id", stored.LastAppliedId);

        WallhavenFeedStore.RecordFetchResult("a", success: true, now: t0.AddMinutes(3));
        Assert.Equal("new-id", Assert.Single(WallhavenFeedStore.ListFeeds()).LastAppliedId);

        WallhavenFeedStore.RecordFetchResult("unknown", success: true, now: t0.AddMinutes(4));
        Assert.Equal(t0.AddMinutes(3), Assert.Single(WallhavenFeedStore.ListFeeds()).LastFetchedUtc);
    }

    [Fact]
    public void RecordFailurePersistsBackoffAndCapsAtFifteenMinutes()
    {
        var t0 = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        WallhavenFeedStore.UpsertFeed(Feed("a", "A"));

        var delays = new[]
        {
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480), TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15)
        };

        for (var failure = 1; failure <= delays.Length; failure++)
        {
            var at = t0.AddMinutes(failure);
            WallhavenFeedStore.RecordFetchResult("a", success: false, now: at);

            // ListFeeds re-reads from disk, so every assertion checks persistence too.
            var stored = Assert.Single(WallhavenFeedStore.ListFeeds());
            Assert.Equal(failure, stored.ConsecutiveFailures);
            Assert.Equal(at + delays[failure - 1], stored.BackoffUntilUtc);
            // A failure must not start the cadence clock; only a success does.
            Assert.Null(stored.LastFetchedUtc);
        }

        WallhavenFeedStore.RecordFetchResult("missing", success: false, now: t0.AddHours(1));
        Assert.Equal(7, Assert.Single(WallhavenFeedStore.ListFeeds()).ConsecutiveFailures);
    }

    [Fact]
    public void CorruptJsonIsQuarantinedAndYieldsEmptyList()
    {
        File.WriteAllText(_path, "{ not json");

        Assert.Empty(WallhavenFeedStore.ListFeeds());

        var backups = Directory.GetFiles(_directory, _fileName + ".corrupt-*");
        var backup = Assert.Single(backups);
        Assert.Matches(@"\.corrupt-\d{8}-\d{6}-[0-9a-f]{8}$", backup);
        Assert.Contains("{ not json", File.ReadAllText(backup));

        File.WriteAllText(_path, "{\"Version\":99}");
        Assert.Empty(WallhavenFeedStore.ListFeeds());
        Assert.Equal(2, Directory.GetFiles(_directory, _fileName + ".corrupt-*").Length);
    }

    [Fact]
    public void FailureBackoffEndsBeforeTheCadenceWould()
    {
        var t0 = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        WallhavenFeedStore.UpsertFeed(Feed("a", "A"));
        WallhavenFeedStore.SetAutoFetchEnabled(true);

        WallhavenFeedStore.RecordFetchResult("a", success: false, now: t0);

        var stored = Assert.Single(WallhavenFeedStore.ListFeeds());
        Assert.Null(stored.LastFetchedUtc);
        Assert.Equal(t0.AddSeconds(30), stored.BackoffUntilUtc);

        // default respectCadence:true: the 30s backoff is the only gate.
        Assert.Null(WallhavenFeedStore.GetNextDueFeed(t0.AddSeconds(10)));
        Assert.Equal("a", WallhavenFeedStore.GetNextDueFeed(t0.AddSeconds(31))!.Id);

        // A success still starts the full cadence window.
        var successAt = t0.AddSeconds(31);
        WallhavenFeedStore.RecordFetchResult("a", success: true, now: successAt);
        Assert.Null(WallhavenFeedStore.GetNextDueFeed(successAt.AddMinutes(59)));
        Assert.Equal("a", WallhavenFeedStore.GetNextDueFeed(successAt.AddMinutes(60))!.Id);
    }

    [Fact]
    public void RememberSearchFiltersPersistsOnlyTheDocumentFilters()
    {
        var t0 = new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero);
        WallhavenFeedStore.SetAutoFetchEnabled(true);
        WallhavenFeedStore.UpsertFeed(new RotationFeed
        {
            Id = "a",
            Name = "A",
            Filters = new WallhavenFilters { Query = "old" },
            CadenceMinutes = 15,
            LastFetchedUtc = t0,
            BackoffUntilUtc = t0.AddMinutes(2),
            ConsecutiveFailures = 2,
            LastAppliedId = "wall-1"
        });
        var before = WallhavenFeedStore.ListFeeds();

        WallhavenFeedStore.RememberSearchFilters(new WallhavenFilters
        {
            Query = "new",
            Categories = WallhavenCategory.Anime
        });

        Assert.Equal(before, WallhavenFeedStore.ListFeeds());
        Assert.Equal("new", WallhavenFeedStore.Load()!.Query);
        Assert.Equal(WallhavenCategory.Anime, WallhavenFeedStore.Load()!.Categories);
        Assert.True(WallhavenFeedStore.IsAutoFetchEnabled());
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void RememberSearchFiltersOnAnEmptyStoreCreatesNoFeed()
    {
        WallhavenFeedStore.RememberSearchFilters(new WallhavenFilters { Query = "sky" });

        Assert.Empty(WallhavenFeedStore.ListFeeds());
        Assert.False(WallhavenFeedStore.IsAutoFetchEnabled());
        Assert.Equal("sky", WallhavenFeedStore.Load()!.Query);
    }

    [Fact]
    public void MixedValidAndInvalidFeedsAreSanitizedOneAtATime()
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var validA = new RotationFeed
        {
            Id = "good-a",
            Name = "Good A",
            Filters = new WallhavenFilters { Query = "sky" },
            CadenceMinutes = 15,
            LastFetchedUtc = t0.AddMinutes(-10),
            BackoffUntilUtc = t0.AddMinutes(2),
            ConsecutiveFailures = 3,
            LastAppliedId = "wall-1"
        };
        var validB = new RotationFeed
        {
            Id = "good-b",
            Name = "Good B",
            Filters = new WallhavenFilters { Query = "city" },
            CadenceMinutes = 75,
            Enabled = false,
            ConsecutiveFailures = 1
        };
        WriteRaw(JsonSerializer.Serialize(new
        {
            Version = 2,
            Filters = new WallhavenFilters { Query = "master" },
            CadenceMinutes = 60,
            AutoFetchEnabled = true,
            Feeds = new object?[]
            {
                validA,
                validA with { Id = "bad id!" },
                null,
                validB,
                validA with { Id = "bad-filters", Filters = new WallhavenFilters { Categories = WallhavenCategory.None } }
            }
        }));

        var feeds = WallhavenFeedStore.ListFeeds();

        Assert.Equal(2, feeds.Count);
        Assert.Equal(validA, feeds[0]);
        Assert.Equal(validB, feeds[1]);
        Assert.True(WallhavenFeedStore.IsAutoFetchEnabled());
    }

    [Fact]
    public void GetNextDueFeedReturnsNullWhenMasterOff()
    {
        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        WallhavenFeedStore.UpsertFeed(Feed("a", "A"));

        Assert.Null(WallhavenFeedStore.GetNextDueFeed(t0));

        WallhavenFeedStore.SetAutoFetchEnabled(true);
        Assert.Equal("a", WallhavenFeedStore.GetNextDueFeed(t0)!.Id);

        WallhavenFeedStore.SetAutoFetchEnabled(false);
        Assert.Null(WallhavenFeedStore.GetNextDueFeed(t0));
    }

    private static RotationFeed Feed(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Filters = new WallhavenFilters { Query = id }
    };

    private string[] Ids() => WallhavenFeedStore.ListFeeds().Select(f => f.Id).ToArray();

    private void WriteRaw(string json) => File.WriteAllText(_path, json);

    private static string V1Json(string? query, int cadenceMinutes, bool autoFetchEnabled) =>
        JsonSerializer.Serialize(new
        {
            Version = 1,
            Filters = query is null ? null : new WallhavenFilters { Query = query },
            CadenceMinutes = cadenceMinutes,
            AutoFetchEnabled = autoFetchEnabled
        });
}
