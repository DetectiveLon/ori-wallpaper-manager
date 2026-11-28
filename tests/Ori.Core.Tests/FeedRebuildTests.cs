using System.Text.Json;
using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Core.Tests;

/// <summary>
/// Feed rebuild coverage: paging state, shared cadence bounds, schema v3 migration
/// and WallhavenQueryBuilder page/seed emission. Store tests share the serial
/// FeedStore collection because the path override is process-wide static state.
/// </summary>
[Collection("FeedStore")]
public sealed class FeedRebuildTests : IDisposable
{
    private readonly string _path;
    private readonly string _directory;
    private readonly string _fileName;

    public FeedRebuildTests()
    {
        _fileName = "ori-feed-rebuild-" + Guid.NewGuid().ToString("N") + ".json";
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

    // ----- RotationFeed paging shape -----

    [Fact]
    public void RotationFeedPagingPropertiesHaveSafeDefaults()
    {
        var feed = new RotationFeed { Id = "a", Name = "A", Filters = new WallhavenFilters() };

        Assert.Equal(1, feed.CurrentPage);
        Assert.Null(feed.LastPage);
        Assert.Null(feed.Seed);
        Assert.Equal(3, feed.MaximumItemsPerFetch);

        feed.Validate(); // safe defaults keep the record valid
    }

    [Fact]
    public void CurrentPageAndLastPageAreClampedToAtLeastOne()
    {
        var feed = new RotationFeed { Id = "a", Name = "A", CurrentPage = 0, LastPage = 0 };

        Assert.Equal(1, feed.CurrentPage);
        Assert.Null(feed.LastPage); // "no known last page" rather than page zero

        var negative = feed with { CurrentPage = -5, LastPage = -3 };
        Assert.Equal(1, negative.CurrentPage);
        Assert.Null(negative.LastPage);
    }

    [Fact]
    public void MaximumItemsPerFetchIsClampedOneToTen()
    {
        Assert.Equal(1, new RotationFeed { MaximumItemsPerFetch = 0 }.MaximumItemsPerFetch);
        Assert.Equal(1, new RotationFeed { MaximumItemsPerFetch = -4 }.MaximumItemsPerFetch);
        Assert.Equal(10, new RotationFeed { MaximumItemsPerFetch = 10 }.MaximumItemsPerFetch);
        Assert.Equal(10, new RotationFeed { MaximumItemsPerFetch = 999 }.MaximumItemsPerFetch);
        Assert.Equal(3, new RotationFeed { MaximumItemsPerFetch = 3 }.MaximumItemsPerFetch);

        // `with` re-applies the clamp.
        var feed = new RotationFeed { MaximumItemsPerFetch = 5 };
        Assert.Equal(10, (feed with { MaximumItemsPerFetch = 42 }).MaximumItemsPerFetch);
        Assert.Equal(1, (feed with { MaximumItemsPerFetch = 0 }).MaximumItemsPerFetch);
    }

    // ----- GetFetchRequest -----

    [Fact]
    public void GetFetchRequestReturnsCurrentPageAndSeed()
    {
        var feed = Feed("a") with { CurrentPage = 3, LastPage = 10, Seed = "abc123", MaximumItemsPerFetch = 5 };

        var request = WallhavenFeedStore.GetFetchRequest(feed, DateTimeOffset.UtcNow);

        Assert.Equal(3, request.Page);
        Assert.Equal("abc123", request.Seed);
    }

    [Fact]
    public void GetFetchRequestClampsCurrentPageIntoKnownLastPage()
    {
        var now = DateTimeOffset.UtcNow;

        var overshoot = Feed("a") with { CurrentPage = 99, LastPage = 4 };
        Assert.Equal(4, WallhavenFeedStore.GetFetchRequest(overshoot, now).Page);

        var floor = Feed("a") with { CurrentPage = 0, LastPage = 4 };
        Assert.Equal(1, WallhavenFeedStore.GetFetchRequest(floor, now).Page);

        // Unknown last page: no upper clamp.
        var unknown = Feed("a") with { CurrentPage = 99, LastPage = null };
        Assert.Equal(99, WallhavenFeedStore.GetFetchRequest(unknown, now).Page);
    }

    [Fact]
    public void GetFetchRequestReturnsNullSeedWhenUnsetOrBlank()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(WallhavenFeedStore.GetFetchRequest(Feed("a"), now).Seed);
        Assert.Null(WallhavenFeedStore.GetFetchRequest(Feed("a") with { Seed = "   " }, now).Seed);
        Assert.Equal("s", WallhavenFeedStore.GetFetchRequest(Feed("a") with { Seed = " s " }, now).Seed);
    }

    [Fact]
    public void GetFetchRequestIsPureAndDoesNotMutateOrPersist()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with { CurrentPage = 2 });
        var before = WallhavenFeedStore.ListFeeds().Single();

        _ = WallhavenFeedStore.GetFetchRequest(before, DateTimeOffset.UtcNow.AddHours(3));

        Assert.Equal(before, WallhavenFeedStore.ListFeeds().Single());
    }

    // ----- RecordFetchPage -----

    [Fact]
    public void RecordFetchPagePersistsUsedPageLastPageAndSeed()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a"));

        WallhavenFeedStore.RecordFetchPage("a", pageUsed: 2, lastPage: 7, seed: "xyz");

        var stored = WallhavenFeedStore.ListFeeds().Single();
        Assert.Equal(3, stored.CurrentPage);   // advances to the next page from the one actually used
        Assert.Equal(7, stored.LastPage);
        Assert.Equal("xyz", stored.Seed);
    }

    [Fact]
    public void RecordFetchPageAdvancesCurrentPageToTheNextPage()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with { CurrentPage = 1 });

        WallhavenFeedStore.RecordFetchPage("a", pageUsed: 1, lastPage: 5, seed: null);

        Assert.Equal(2, WallhavenFeedStore.ListFeeds().Single().CurrentPage);

        WallhavenFeedStore.RecordFetchPage("a", pageUsed: 2, lastPage: 5, seed: null);

        Assert.Equal(3, WallhavenFeedStore.ListFeeds().Single().CurrentPage);
    }

    [Fact]
    public void RecordFetchPageWrapsToOneAfterTheLastPage()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with { CurrentPage = 5, LastPage = 5 });

        WallhavenFeedStore.RecordFetchPage("a", pageUsed: 5, lastPage: 5, seed: null);

        Assert.Equal(1, WallhavenFeedStore.ListFeeds().Single().CurrentPage);

        // A page past the reported end still wraps.
        WallhavenFeedStore.RecordFetchPage("a", pageUsed: 4, lastPage: 4, seed: null);
        Assert.Equal(1, WallhavenFeedStore.ListFeeds().Single().CurrentPage);
    }

    [Fact]
    public void RecordFetchPageKeepsWalkingWhenLastPageIsUnknown()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with { CurrentPage = 1 });

        WallhavenFeedStore.RecordFetchPage("a", pageUsed: 1, lastPage: null, seed: null);

        var stored = WallhavenFeedStore.ListFeeds().Single();
        Assert.Equal(2, stored.CurrentPage);
        Assert.Null(stored.LastPage);
    }

    [Fact]
    public void RecordFetchPageIgnoresUnknownOrBlankIds()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a"));

        WallhavenFeedStore.RecordFetchPage("missing", pageUsed: 3, lastPage: 3, seed: "s");
        WallhavenFeedStore.RecordFetchPage("  ", pageUsed: 3, lastPage: 3, seed: "s");

        var stored = WallhavenFeedStore.ListFeeds().Single();
        Assert.Equal(1, stored.CurrentPage);
        Assert.Null(stored.LastPage);
        Assert.Null(stored.Seed);
        Assert.Equal(3, stored.MaximumItemsPerFetch);
    }

    [Fact]
    public void FeedWalksPagesInsteadOfRefetchingPageOneForever()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with { CurrentPage = 1 });

        for (var expected = 1; expected <= 3; expected++)
        {
            var request = WallhavenFeedStore.GetFetchRequest(WallhavenFeedStore.ListFeeds().Single(), DateTimeOffset.UtcNow);
            Assert.Equal(expected, request.Page);
            WallhavenFeedStore.RecordFetchPage("a", request.Page, lastPage: 3, seed: request.Seed);
        }

        // Wrapped back to page 1 after the last page.
        var wrapped = WallhavenFeedStore.GetFetchRequest(WallhavenFeedStore.ListFeeds().Single(), DateTimeOffset.UtcNow);
        Assert.Equal(1, wrapped.Page);
    }

    // ----- shared cadence -----

    [Fact]
    public void FeedCadenceChoicesAreSharedAndBoundedFifteenTo10080()
    {
        var choices = WallhavenFeedStore.FeedCadenceChoices;

        Assert.NotEmpty(choices);
        Assert.Equal(WallhavenFeedStore.MinimumFeedCadenceMinutes, choices.Min());
        Assert.Equal(WallhavenFeedStore.MaximumFeedCadenceMinutes, choices.Max());
        Assert.Equal(15, WallhavenFeedStore.MinimumFeedCadenceMinutes);
        Assert.Equal(10080, WallhavenFeedStore.MaximumFeedCadenceMinutes);
        Assert.Equal(choices.OrderBy(c => c), choices);
    }

    [Fact]
    public void BuildCadenceChoicesAddsCustomValueAndClamps()
    {
        var choices = WallhavenFeedStore.BuildCadenceChoices(47);

        Assert.Contains(47, choices);
        Assert.Equal(choices.OrderBy(c => c), choices);
        Assert.Equal(WallhavenFeedStore.MinimumFeedCadenceMinutes, choices.Min());
        Assert.Equal(WallhavenFeedStore.MaximumFeedCadenceMinutes, choices.Max());

        // Out-of-range custom values clamp into the shared range.
        Assert.Equal(WallhavenFeedStore.MinimumFeedCadenceMinutes, WallhavenFeedStore.BuildCadenceChoices(1).Min());
        Assert.Equal(WallhavenFeedStore.MaximumFeedCadenceMinutes, WallhavenFeedStore.BuildCadenceChoices(99999).Max());
    }

    [Fact]
    public void ClampFeedCadenceClampsIntoSharedRange()
    {
        Assert.Equal(15, WallhavenFeedStore.ClampFeedCadence(0));
        Assert.Equal(15, WallhavenFeedStore.ClampFeedCadence(14));
        Assert.Equal(15, WallhavenFeedStore.ClampFeedCadence(15));
        Assert.Equal(10080, WallhavenFeedStore.ClampFeedCadence(10080));
        Assert.Equal(10080, WallhavenFeedStore.ClampFeedCadence(10081));
        Assert.Equal(10080, WallhavenFeedStore.ClampFeedCadence(int.MaxValue));
        Assert.Equal(500, WallhavenFeedStore.ClampFeedCadence(500));
    }

    // ----- WallhavenQueryBuilder page/seed emission -----

    [Fact]
    public void QueryBuilderEmitsPageAndSeed()
    {
        var filters = new WallhavenFilters { Query = "sky", Page = 4, Seed = "seed42" };

        var query = WallhavenQueryBuilder.Build(filters).Query;

        Assert.Contains("page=4", query);
        Assert.Contains("seed=seed42", query);
    }

    [Fact]
    public void QueryBuilderPageDefaultsToOneAndOmitsEmptySeed()
    {
        var query = WallhavenQueryBuilder.Build(new WallhavenFilters()).Query;

        Assert.Contains("page=1", query);
        Assert.DoesNotContain("seed=", query);
    }

    [Fact]
    public void QueryBuilderOverloadEmitsExplicitPageAndSeedWithoutMutatingFilters()
    {
        var filters = new WallhavenFilters { Query = "sky", Page = 1, Seed = "original" };

        var query = WallhavenQueryBuilder.Build(filters, page: 7, seed: "rotated").Query;

        Assert.Contains("page=7", query);
        Assert.Contains("seed=rotated", query);
        // Source filters are untouched.
        Assert.Equal(1, filters.Page);
        Assert.Equal("original", filters.Seed);
    }

    [Fact]
    public void QueryBuilderBuildFetchUsesGetFetchRequestShape()
    {
        var feed = Feed("a") with
        {
            CurrentPage = 3,
            LastPage = 9,
            Seed = "s3ed",
            Filters = new WallhavenFilters { Query = "nature", Page = 1, Seed = null }
        };

        var request = WallhavenFeedStore.GetFetchRequest(feed, DateTimeOffset.UtcNow);
        var query = WallhavenQueryBuilder.BuildFetch(feed, request).Query;

        Assert.Contains("page=3", query);
        Assert.Contains("seed=s3ed", query);
        Assert.Contains("q=nature", query);
    }

    // ----- schema v3 + v2 migration -----

    [Fact]
    public void SchemaVersionIsThreeAndWritesV3()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with { CurrentPage = 2, LastPage = 6, Seed = "s", MaximumItemsPerFetch = 7 });

        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(3, document.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void V2DocumentMigratesToV3WithSafePagingDefaults()
    {
        // v2 has no paging state at all; also sprinkle unknown fields to prove tolerance.
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            Version = 2,
            Filters = new WallhavenFilters { Query = "master" },
            CadenceMinutes = 60,
            AutoFetchEnabled = true,
            UnknownFutureField = "ignore me",
            Feeds = new object[]
            {
                new
                {
                    Id = "a", Name = "A", Filters = new WallhavenFilters { Query = "sky" },
                    CadenceMinutes = 30, Enabled = true, ConsecutiveFailures = 0,
                    AnotherUnknown = 123
                }
            }
        }));

        var feed = WallhavenFeedStore.ListFeeds().Single();

        Assert.Equal("a", feed.Id);
        Assert.Equal("sky", feed.Filters.Query);
        Assert.Equal(30, feed.CadenceMinutes);
        // v2 has no paging -> safe defaults.
        Assert.Equal(1, feed.CurrentPage);
        Assert.Null(feed.LastPage);
        Assert.Null(feed.Seed);
        Assert.Equal(3, feed.MaximumItemsPerFetch);
        Assert.True(WallhavenFeedStore.IsAutoFetchEnabled());

        // A write promotes the record to v3.
        WallhavenFeedStore.SetAutoFetchEnabled(true);
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(3, document.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void V2PagingFieldsAbsentStayDefaultsAndOutOfRangeValuesAreSanitized()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            Version = 2,
            Filters = (WallhavenFilters?)null,
            CadenceMinutes = 60,
            AutoFetchEnabled = false,
            Feeds = new object[]
            {
                new
                {
                    Id = "a", Name = "A", Filters = new WallhavenFilters { Query = "sky" },
                    CadenceMinutes = 30, Enabled = true,
                    CurrentPage = -4, LastPage = 0, MaximumItemsPerFetch = 99, Seed = "  "
                }
            }
        }));

        var feed = WallhavenFeedStore.ListFeeds().Single();

        Assert.Equal(1, feed.CurrentPage);
        Assert.Null(feed.LastPage);
        Assert.Equal(10, feed.MaximumItemsPerFetch);
        Assert.Null(feed.Seed);
    }

    [Fact]
    public void V3RoundTripsPagingState()
    {
        WallhavenFeedStore.UpsertFeed(Feed("a") with
        {
            CurrentPage = 4,
            LastPage = 12,
            Seed = "persisted",
            MaximumItemsPerFetch = 8
        });

        var stored = WallhavenFeedStore.ListFeeds().Single();

        Assert.Equal(4, stored.CurrentPage);
        Assert.Equal(12, stored.LastPage);
        Assert.Equal("persisted", stored.Seed);
        Assert.Equal(8, stored.MaximumItemsPerFetch);
    }

    [Fact]
    public void UnknownEnumStringsFallBackWithoutQuarantiningTheStore()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            Version = 3,
            Filters = (WallhavenFilters?)null,
            CadenceMinutes = 60,
            AutoFetchEnabled = true,
            Feeds = new object[]
            {
                new
                {
                    Id = "a", Name = "A", CadenceMinutes = 30, Enabled = true,
                    Filters = new
                    {
                        Query = "sky",
                        Sorting = "FromTheFuture",
                        Order = "FromTheFuture",
                        TopRange = "FromTheFuture",
                        Categories = "General, Anime"
                    }
                }
            }
        }));

        var feed = WallhavenFeedStore.ListFeeds().Single();

        Assert.Equal(WallhavenSort.Random, feed.Filters.Sorting);
        Assert.Equal(WallhavenOrder.Descending, feed.Filters.Order);
        Assert.Equal(WallhavenTopRange.Month, feed.Filters.TopRange);
        Assert.Equal(WallhavenCategory.General | WallhavenCategory.Anime, feed.Filters.Categories);
        Assert.False(Directory.GetFiles(_directory, _fileName + ".corrupt-*").Any());
    }

    private static RotationFeed Feed(string id) => new()
    {
        Id = id,
        Name = id,
        Filters = new WallhavenFilters { Query = id }
    };
}
