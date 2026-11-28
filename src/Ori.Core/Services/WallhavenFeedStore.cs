using System.Text.Json;
using System.Text.Json.Serialization;
using Ori.Core.Models;

namespace Ori.Core.Services;

/// <summary>
/// Persists the Discover search preferences used by hands-free Wallhaven rotation.
/// Version 3 stores a list of independently scheduled feeds, each carrying its own
/// paging state (<see cref="RotationFeed.CurrentPage"/>, <see cref="RotationFeed.LastPage"/>,
/// <see cref="RotationFeed.Seed"/>, <see cref="RotationFeed.MaximumItemsPerFetch"/>).
/// Version 1 records are migrated in memory to a single "Saved search" feed; version 2
/// records are promoted in place. Unknown fields are ignored and a corrupt file is
/// quarantined. The record is written atomically so a crash can never leave a
/// half-written preference file; a missing or corrupt record is simply reported as
/// "no feed". The opt-in flag lives in the same record so enabling auto-fetch survives
/// restarts.
/// </summary>
public static class WallhavenFeedStore
{
    private const int SchemaVersion = 3;
    private const long MaximumDocumentBytes = 1024 * 1024;
    public const int MinimumCadenceMinutes = 1;
    public const int MaximumCadenceMinutes = 7 * 24 * 60; // one week
    public const int MaximumFeeds = 20;
    public const int DefaultCadenceMinutes = 60;
    public const string MigratedFeedId = "saved-search";
    public const string MigratedFeedName = "Saved search";
    public const int MaximumFailures = 20;
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Product floor for a feed's fetch cadence. Core validation still accepts any
    /// whole minute, but every cadence editor offers and clamps to this range.
    /// </summary>
    public const int MinimumFeedCadenceMinutes = 15;
    /// <summary>Product ceiling for a feed's fetch cadence (one week).</summary>
    public const int MaximumFeedCadenceMinutes = 7 * 24 * 60;

    /// <summary>
    /// One shared cadence choice list driven by Core bounds (15..10080 minutes).
    /// Custom numeric entry is supported separately by <see cref="ClampFeedCadence"/>.
    /// </summary>
    public static IReadOnlyList<int> FeedCadenceChoices { get; } = new[]
    {
        MinimumFeedCadenceMinutes, 30, 60, 120, 240, 360, 720, 1440, MaximumFeedCadenceMinutes
    };

    /// <summary>Clamps a cadence into the shared 15..10080 minute feed range.</summary>
    public static int ClampFeedCadence(int minutes) =>
        Math.Clamp(minutes, MinimumFeedCadenceMinutes, MaximumFeedCadenceMinutes);

    /// <summary>
    /// Shared list of choices for a ComboBox: the standard cadences plus any custom
    /// value already in use, clamped into range, in ascending order.
    /// </summary>
    public static IReadOnlyList<int> BuildCadenceChoices(int currentMinutes)
    {
        var choices = new SortedSet<int>(FeedCadenceChoices);
        choices.Add(ClampFeedCadence(currentMinutes));
        return choices.ToArray();
    }

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "rotation-feed.json");

    private static readonly object SyncRoot = new();
    private static string? _pathOverride;
    private static bool _migrationPending;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new TolerantEnumConverterFactory(
                (typeof(WallhavenSort), WallhavenSort.Random),
                (typeof(WallhavenOrder), WallhavenOrder.Descending),
                (typeof(WallhavenTopRange), WallhavenTopRange.Month))
        }
    };

    /// <summary>Saves the current Discover filters and their fetch cadence.</summary>
    public static void Save(WallhavenFilters filters, int cadenceMinutes)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (cadenceMinutes < MinimumCadenceMinutes || cadenceMinutes > MaximumCadenceMinutes)
            throw new ArgumentOutOfRangeException(nameof(cadenceMinutes));

        // Validate here as well as in Discover: a feed that cannot produce a legal
        // query must never be handed to the rotation loop.
        filters.Validate();

        lock (SyncRoot)
        {
            var existing = ReadCore();
            var feeds = existing?.Feeds?.ToList() ?? new List<RotationFeed>();
            var migratedIndex = feeds.FindIndex(f => string.Equals(
                f.Id, MigratedFeedId, StringComparison.OrdinalIgnoreCase));

            // A legacy save only ever owns its one migrated feed. Any user-created
            // feed, and every piece of rotation state it has accumulated, is left alone.
            var migrated = migratedIndex >= 0
                ? feeds[migratedIndex] with
                {
                    Name = MigratedFeedName,
                    Filters = filters,
                    CadenceMinutes = cadenceMinutes,
                    Enabled = true
                }
                : new RotationFeed
                {
                    Id = MigratedFeedId,
                    Name = MigratedFeedName,
                    Filters = filters,
                    CadenceMinutes = cadenceMinutes,
                    Enabled = true
                };

            if (migratedIndex >= 0)
            {
                feeds[migratedIndex] = migrated;
            }
            else
            {
                if (feeds.Count >= MaximumFeeds)
                    throw new InvalidOperationException("The rotation list already holds the maximum number of feeds.");
                feeds.Add(migrated);
            }

            var document = new FeedDocument(
                SchemaVersion,
                filters,
                cadenceMinutes,
                existing?.AutoFetchEnabled ?? false,
                feeds);
            Persist(document, changed: true);
        }
    }

    /// <summary>Returns the saved filters, or null when no valid feed has been saved.</summary>
    public static WallhavenFilters? Load() => LoadWithCadence()?.Filters;

    /// <summary>Returns the saved filters together with their cadence, or null when absent/corrupt.</summary>
    public static (WallhavenFilters Filters, int CadenceMinutes)? LoadWithCadence()
    {
        lock (SyncRoot)
        {
            var document = ReadCore();
            if (document is null || document.Version != SchemaVersion)
                return null;

            // The document-level filters are Discover's current search state. An
            // ordinary search updates them without touching any feed, so they must
            // win over a feed's own filters. A v1 migration stores the same filters
            // in both places, so legacy records keep their behaviour.
            var documentFilters = Normalize(document.Filters);
            if (documentFilters is not null)
            {
                var cadence = document.CadenceMinutes >= MinimumCadenceMinutes
                              && document.CadenceMinutes <= MaximumCadenceMinutes
                    ? document.CadenceMinutes
                    : DefaultCadenceMinutes;
                return (documentFilters, cadence);
            }

            if (document.Feeds is { Count: > 0 })
            {
                var first = document.Feeds[0];
                var filters = Normalize(first.Filters);
                if (filters is null)
                    return null;
                var feedCadence = Math.Clamp(first.CadenceMinutes, MinimumCadenceMinutes, MaximumCadenceMinutes);
                return (filters, feedCadence);
            }

            return null;
        }
    }

    /// <summary>
    /// Persists only the document-level Discover filters. Feeds, their cadence, their
    /// enabled flag and all accumulated fetch state stay exactly as they are on disk.
    /// </summary>
    public static void RememberSearchFilters(WallhavenFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var normalized = Normalize(filters)
            ?? throw new ArgumentException("Invalid Discover filters.", nameof(filters));

        lock (SyncRoot)
        {
            // A raw read is deliberate: ReadCore() migrates legacy records and sanitizes
            // feeds in memory, and persisting that result would create/rewrite feeds.
            var existing = ReadDocumentRaw();
            var document = existing is null
                ? new FeedDocument(SchemaVersion, normalized, DefaultCadenceMinutes, false, null)
                : existing with { Filters = normalized };
            WriteCore(document);
        }
    }

    /// <summary>Whether the user opted in to automatic Wallhaven fetching.</summary>
    public static bool IsAutoFetchEnabled()
    {
        lock (SyncRoot)
            return ReadCore()?.AutoFetchEnabled ?? false;
    }

    /// <summary>Persists the user's opt-in choice without touching the saved filters.</summary>
    public static void SetAutoFetchEnabled(bool enabled)
    {
        lock (SyncRoot)
        {
            var existing = ReadCore();
            if (existing is null)
            {
                // No saved Discover filters yet; store the preference in an empty
                // v3 record so it survives until the first successful search saves them.
                Persist(new FeedDocument(SchemaVersion, null, 0, enabled, null), changed: true);
                return;
            }

            if (existing.AutoFetchEnabled == enabled)
            {
                // A migrated legacy record is still only in memory; promote it now.
                Persist(existing with { Version = SchemaVersion }, changed: false);
                return;
            }

            Persist(existing with { AutoFetchEnabled = enabled }, changed: true);
        }
    }

    /// <summary>Returns a snapshot of the feeds in their current order.</summary>
    public static IReadOnlyList<RotationFeed> ListFeeds()
    {
        lock (SyncRoot)
        {
            var document = ReadCore();
            return document?.Feeds is { } feeds ? feeds.ToArray() : Array.Empty<RotationFeed>();
        }
    }

    /// <summary>Adds a feed, or replaces an existing one with the same id, in place.</summary>
    public static void UpsertFeed(RotationFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        feed.Validate();

        lock (SyncRoot)
        {
            var document = ReadCore();
            var feeds = document?.Feeds?.ToList() ?? new List<RotationFeed>();
            var id = feed.Id.Trim();
            var index = feeds.FindIndex(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            var stored = feed with { Id = id, Name = feed.Name.Trim() };

            if (index >= 0)
            {
                feeds[index] = stored;
            }
            else
            {
                if (feeds.Count >= MaximumFeeds)
                    throw new InvalidOperationException("The rotation list already holds the maximum number of feeds.");
                feeds.Add(stored);
            }

            Persist((document ?? new FeedDocument(SchemaVersion, null, 0, false, null)) with
            {
                Version = SchemaVersion,
                Feeds = feeds
            }, changed: true);
        }
    }

    /// <summary>Removes a feed by id. Unknown or blank ids change nothing.</summary>
    public static void RemoveFeed(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (SyncRoot)
        {
            var document = ReadCore();
            var feeds = document?.Feeds?.ToList();
            if (feeds is null)
                return;

            var removed = feeds.RemoveAll(f => string.Equals(
                f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return;

            Persist(document! with { Feeds = feeds }, changed: true);
        }
    }

    /// <summary>Moves a feed to an index, clamped to the bounds of the list.</summary>
    public static void MoveFeed(string id, int newIndex)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (SyncRoot)
        {
            var document = ReadCore();
            var feeds = document?.Feeds?.ToList();
            if (feeds is null || feeds.Count == 0)
                return;

            var index = feeds.FindIndex(f => string.Equals(
                f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            var target = Math.Clamp(newIndex, 0, feeds.Count - 1);
            if (target == index)
                return;

            var feed = feeds[index];
            feeds.RemoveAt(index);
            feeds.Insert(target, feed);
            Persist(document! with { Feeds = feeds }, changed: true);
        }
    }

    /// <summary>Returns the next eligible feed when auto-fetch is enabled, else null.</summary>
    public static RotationFeed? GetNextDueFeed(DateTimeOffset now)
    {
        lock (SyncRoot)
        {
            var document = ReadCore();
            if (document is null || !document.AutoFetchEnabled)
                return null;

            return SelectNextFeed(document.Feeds ?? Array.Empty<RotationFeed>(), now);
        }
    }

    /// <summary>Records the outcome of a fetch, including throttling after failures.</summary>
    public static void RecordFetchResult(string id, bool success, DateTimeOffset now, string? appliedId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (SyncRoot)
        {
            var document = ReadCore();
            var feeds = document?.Feeds?.ToList();
            if (feeds is null)
                return;

            var index = feeds.FindIndex(f => string.Equals(
                f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            var feed = feeds[index];
            if (success)
            {
                feeds[index] = feed with
                {
                    LastFetchedUtc = now,
                    ConsecutiveFailures = 0,
                    BackoffUntilUtc = null,
                    LastAppliedId = appliedId ?? feed.LastAppliedId
                };
            }
            else
            {
                var failures = Math.Min(feed.ConsecutiveFailures + 1, MaximumFailures);
                var delaySeconds = Math.Min(
                    30L * (1L << Math.Min(failures - 1, 10)), (long)MaximumBackoff.TotalSeconds);
                // Do not touch LastFetchedUtc here: SelectNextFeed also enforces the
                // cadence from that timestamp, so stamping it on a failure turns a
                // 30-second backoff into a full cadence wait.
                feeds[index] = feed with
                {
                    ConsecutiveFailures = failures,
                    BackoffUntilUtc = now + TimeSpan.FromSeconds(delaySeconds)
                };
            }

            Persist(document! with { Feeds = feeds }, changed: true);
        }
    }

    // ----- paging (contract: Feed agent -> Automation agent) -----

    /// <summary>
    /// The page and seed a fetch should use for this feed.
    /// Returns the feed's <see cref="RotationFeed.CurrentPage"/> clamped into
    /// 1..<see cref="RotationFeed.LastPage"/> when the last page is known, plus the
    /// feed's stored seed. Pure: no file access and no mutation.
    /// </summary>
    public static (int Page, string? Seed) GetFetchRequest(RotationFeed feed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(feed);

        var page = feed.CurrentPage < RotationFeed.FirstPage ? RotationFeed.FirstPage : feed.CurrentPage;
        if (feed.LastPage is { } lastPage && lastPage >= RotationFeed.FirstPage && page > lastPage)
            page = lastPage;

        var seed = feed.Seed;
        if (string.IsNullOrWhiteSpace(seed))
            seed = null;
        else
        {
            var trimmed = seed.Trim();
            seed = trimmed.Length > RotationFeed.MaximumSeedLength
                ? trimmed[..RotationFeed.MaximumSeedLength] : trimmed;
        }

        // 'now' is accepted for symmetry with the rest of the store; cadence is
        // enforced by SelectNextFeed, not by paging.
        _ = now;
        return (page, seed);
    }

    /// <summary>
    /// Records the page actually used for a fetch, the last page Wallhaven reported
    /// and its seed, then advances <see cref="RotationFeed.CurrentPage"/> to the next
    /// page, wrapping to 1 after the last page. Unknown or blank ids change nothing.
    /// </summary>
    public static void RecordFetchPage(string id, int pageUsed, int? lastPage, string? seed)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        var used = pageUsed < RotationFeed.FirstPage ? RotationFeed.FirstPage : pageUsed;
        int? reportedLast = lastPage is { } page && page >= RotationFeed.FirstPage ? page : null;

        string? trimmedSeed = seed?.Trim();
        if (string.IsNullOrEmpty(trimmedSeed))
            trimmedSeed = null;
        else if (trimmedSeed.Length > RotationFeed.MaximumSeedLength)
            trimmedSeed = trimmedSeed[..RotationFeed.MaximumSeedLength];

        lock (SyncRoot)
        {
            var document = ReadCore();
            var feeds = document?.Feeds?.ToList();
            if (feeds is null)
                return;

            var index = feeds.FindIndex(f => string.Equals(
                f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            var feed = feeds[index];
            var effectiveLast = reportedLast ?? feed.LastPage;

            int nextPage;
            if (effectiveLast is { } last && used >= last)
                nextPage = RotationFeed.FirstPage;                       // wrap after the last page
            else if (effectiveLast is { } bound && used + 1 > bound)
                nextPage = RotationFeed.FirstPage;
            else
                nextPage = used + 1;

            feeds[index] = feed with
            {
                CurrentPage = nextPage,
                LastPage = effectiveLast,
                Seed = trimmedSeed ?? feed.Seed
            };

            Persist(document! with { Feeds = feeds }, changed: true);
        }
    }

    /// <summary>
    /// Round-robin selection over enabled feeds, starting just after the one fetched
    /// most recently. No file access and no mutation: callers can use it for previews.
    /// </summary>
    public static RotationFeed? SelectNextFeed(
        IReadOnlyList<RotationFeed> feeds,
        DateTimeOffset now,
        bool respectCadence = true,
        bool respectBackoff = true)
    {
        ArgumentNullException.ThrowIfNull(feeds);

        var enabled = feeds.Where(f => f is not null && f.Enabled).ToArray();
        if (enabled.Length == 0)
            return null;

        var cursor = -1;
        DateTimeOffset latest = default;
        for (var i = 0; i < enabled.Length; i++)
        {
            var last = enabled[i].LastFetchedUtc;
            if (last.HasValue && (cursor < 0 || last.Value > latest))
            {
                latest = last.Value;
                cursor = i;
            }
        }

        for (var step = 1; step <= enabled.Length; step++)
        {
            var feed = enabled[(cursor + step) % enabled.Length];
            if (respectBackoff && feed.BackoffUntilUtc.HasValue && now < feed.BackoffUntilUtc.Value)
                continue;
            if (respectCadence && feed.LastFetchedUtc.HasValue)
            {
                // A backwards clock jump must not turn elapsed time into a negative
                // value that can hide the cadence check.
                var elapsed = now - feed.LastFetchedUtc.Value;
                if (elapsed < TimeSpan.Zero)
                    elapsed = TimeSpan.Zero;
                if (elapsed < TimeSpan.FromMinutes(feed.CadenceMinutes))
                    continue;
            }
            return feed;
        }

        return null;
    }

    // ----- test seam: the path is process-wide, so tests serialize via a collection -----

    internal static void SetPathForTests(string? path)
    {
        lock (SyncRoot)
            _pathOverride = path;
    }

    internal static void ResetPathForTests() => SetPathForTests(null);

    internal static string ResolvePath() => _pathOverride ?? Path;

    // ----- persistence -----

    /// <summary>
    /// Reads the file exactly as stored, without legacy migration or feed sanitization.
    /// Used by operations that must not change feeds as a side effect.
    /// </summary>
    private static FeedDocument? ReadDocumentRaw()
    {
        var target = ResolvePath();
        if (!File.Exists(target))
            return null;

        try
        {
            // RememberSearchFilters overwrites this record unconditionally. A file that
            // exists but cannot be read must be kept, or every saved feed disappears.
            if (new FileInfo(target).Length > MaximumDocumentBytes)
            {
                Quarantine();
                return null;
            }

            FeedDocument? document;
            using (var stream = new FileStream(
                       target, FileMode.Open, FileAccess.Read, FileShare.Read,
                       16 * 1024, FileOptions.SequentialScan))
            {
                document = JsonSerializer.Deserialize<FeedDocument>(stream, JsonOptions);
            }
            // The stream must be closed before Quarantine moves the file on Windows.
            if (document is null)
            {
                Quarantine();
                return null;
            }
            return document;
        }
        catch (JsonException)
        {
            Quarantine();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the record, migrating a v1 or v2 record in memory. A migration is marked
    /// pending so the next write promotes the file to v3.
    /// </summary>
    private static FeedDocument? ReadCore()
    {
        try
        {
            var target = ResolvePath();
            if (!File.Exists(target))
                return null;
            if (new FileInfo(target).Length > MaximumDocumentBytes)
            {
                Quarantine();
                return null;
            }

            FeedDocument? document;
            using (var stream = new FileStream(
                target, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan))
            {
                document = JsonSerializer.Deserialize<FeedDocument>(stream, JsonOptions);
            }
            // The stream must be closed before Quarantine moves the file on Windows.
            if (document is null)
            {
                Quarantine();
                return null;
            }

            if (document.Version == 1)
            {
                var feeds = new List<RotationFeed>();
                if (document.Filters is not null)
                {
                    var filters = Normalize(document.Filters);
                    if (filters is not null)
                    {
                        var cadence = document.CadenceMinutes >= MinimumCadenceMinutes
                                      && document.CadenceMinutes <= MaximumCadenceMinutes
                            ? document.CadenceMinutes
                            : DefaultCadenceMinutes;
                        feeds.Add(new RotationFeed
                        {
                            Id = MigratedFeedId,
                            Name = MigratedFeedName,
                            Filters = filters,
                            CadenceMinutes = cadence,
                            Enabled = true
                        });
                    }
                }

                _migrationPending = true;
                return document with { Version = SchemaVersion, Feeds = feeds };
            }

            if (document.Version == 2)
            {
                // v2 has no paging state; new fields take their safe defaults.
                _migrationPending = true;
                return document with
                {
                    Version = SchemaVersion,
                    Feeds = NormalizeFeeds(document.Feeds, DateTimeOffset.UtcNow)
                };
            }

            if (document.Version != SchemaVersion)
            {
                // An unreadable record would otherwise be silently overwritten by the
                // next save, losing the user's Discover filters and opt-in for good.
                Quarantine();
                return null;
            }

            return document with { Feeds = NormalizeFeeds(document.Feeds, DateTimeOffset.UtcNow) };
        }
        catch (JsonException)
        {
            Quarantine();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes through when something changed, or when a legacy record needs promoting.</summary>
    private static void Persist(FeedDocument? document, bool changed)
    {
        if (document is not null && (changed || _migrationPending))
            WriteCore(document with { Version = SchemaVersion });
        _migrationPending = false;
    }

    /// <summary>Keeps a copy of an unreadable feed file so the next save cannot erase it.</summary>
    private static void Quarantine()
    {
        var target = ResolvePath();
        var backup = target + ".corrupt-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Move(target, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing better is possible here; the next save will replace the file.
        }
    }

    private static void WriteCore(FeedDocument document)
    {
        var target = ResolvePath();
        var directory = System.IO.Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static WallhavenFilters? Normalize(WallhavenFilters? filters)
    {
        if (filters is null)
            return null;

        var normalized = filters with
        {
            Query = filters.Query ?? string.Empty,
            Colors = (filters.Colors ?? Array.Empty<string>())
                .Where(color => !string.IsNullOrWhiteSpace(color))
                .ToArray(),
            // Clamp unknown enums to documented defaults instead of discarding a
            // whole feed (or Discover's saved search) when it is read back.
            Sorting = Enum.IsDefined(filters.Sorting) ? filters.Sorting : WallhavenSort.Random,
            Order = Enum.IsDefined(filters.Order) ? filters.Order : WallhavenOrder.Descending,
            TopRange = Enum.IsDefined(filters.TopRange) ? filters.TopRange : WallhavenTopRange.Month,
            MinimumResolution = string.IsNullOrWhiteSpace(filters.MinimumResolution) ? null : filters.MinimumResolution,
            ExactResolution = string.IsNullOrWhiteSpace(filters.ExactResolution) ? null : filters.ExactResolution,
            Ratio = string.IsNullOrWhiteSpace(filters.Ratio) ? null : filters.Ratio,
            Seed = string.IsNullOrWhiteSpace(filters.Seed) ? null : filters.Seed
        };

        try
        {
            normalized.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Sanitizes a persisted feed list one entry at a time: a single bad feed is
    /// dropped instead of making the whole record unreadable.
    /// </summary>
    private static IReadOnlyList<RotationFeed>? NormalizeFeeds(
        IReadOnlyList<RotationFeed>? feeds,
        DateTimeOffset now)
    {
        if (feeds is null)
            return null;

        var normalized = new List<RotationFeed>(Math.Min(feeds.Count, MaximumFeeds));
        foreach (var candidate in feeds)
        {
            if (candidate is null)
                continue;

            var id = candidate.Id?.Trim();
            if (!RotationFeed.IsSafeToken(id, RotationFeed.MaximumIdLength))
                continue;

            WallhavenFilters? filters;
            try
            {
                filters = Normalize(candidate.Filters);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                continue;
            }
            if (filters is null)
                continue;

            var name = string.IsNullOrWhiteSpace(candidate.Name)
                ? id!
                : candidate.Name.Trim();
            if (name.Length > RotationFeed.MaximumNameLength)
                name = name[..RotationFeed.MaximumNameLength];

            normalized.Add(candidate with
            {
                Id = id!,
                Name = name,
                Filters = filters,
                CadenceMinutes = Math.Clamp(candidate.CadenceMinutes, MinimumCadenceMinutes, MaximumCadenceMinutes),
                ConsecutiveFailures = Math.Clamp(candidate.ConsecutiveFailures, 0, MaximumFailures),
                // A clock that jumped backwards must not freeze selection/backoff.
                LastFetchedUtc = ClampToNow(candidate.LastFetchedUtc, now),
                BackoffUntilUtc = ClampToNow(candidate.BackoffUntilUtc, now),
                LastAppliedId = RotationFeed.IsSafeToken(
                    candidate.LastAppliedId, RotationFeed.MaximumAppliedIdLength, allowEmpty: true)
                    ? candidate.LastAppliedId
                    : null,
                // Paging state is sanitized rather than dropping the feed.
                CurrentPage = candidate.CurrentPage < RotationFeed.FirstPage
                    ? RotationFeed.FirstPage : candidate.CurrentPage,
                LastPage = candidate.LastPage is { } lastPage && lastPage >= RotationFeed.FirstPage
                    ? lastPage : null,
                MaximumItemsPerFetch = RotationFeed.ClampMaximumItemsPerFetch(candidate.MaximumItemsPerFetch)
            });

            if (normalized.Count == MaximumFeeds)
                break;
        }
        return normalized;
    }

    private static DateTimeOffset? ClampToNow(DateTimeOffset? value, DateTimeOffset now) =>
        value.HasValue && value.Value > now ? now : value;

    private sealed record FeedDocument(
        int Version,
        WallhavenFilters? Filters,
        int CadenceMinutes,
        bool AutoFetchEnabled,
        IReadOnlyList<RotationFeed>? Feeds);
}
