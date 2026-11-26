using System.Text.Json;
using System.Text.Json.Serialization;
using Ori.Core.Models;

namespace Ori.Core.Services;

/// <summary>
/// Persists the Discover search preferences used by hands-free Wallhaven rotation.
/// Version 2 stores a list of independently scheduled feeds; version 1 records are
/// migrated in memory to a single "Saved search" feed and promoted on the next write.
/// The record is written atomically so a crash can never leave a half-written
/// preference file; a missing or corrupt record is simply reported as "no feed".
/// The opt-in flag lives in the same record so enabling auto-fetch survives restarts.
/// </summary>
public static class WallhavenFeedStore
{
    private const int SchemaVersion = 2;
    public const int MinimumCadenceMinutes = 1;
    public const int MaximumCadenceMinutes = 7 * 24 * 60; // one week
    public const int MaximumFeeds = 20;
    public const int DefaultCadenceMinutes = 60;
    public const string MigratedFeedId = "saved-search";
    public const string MigratedFeedName = "Saved search";
    public const int MaximumFailures = 20;
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "rotation-feed.json");

    private static readonly object SyncRoot = new();
    private static string? _pathOverride;
    private static bool _migrationPending;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
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

            if (document.Feeds is { Count: > 0 })
            {
                var first = document.Feeds[0];
                var filters = Normalize(first.Filters);
                if (filters is null)
                    return null;
                var feedCadence = Math.Clamp(first.CadenceMinutes, MinimumCadenceMinutes, MaximumCadenceMinutes);
                return (filters, feedCadence);
            }

            if (document.CadenceMinutes < MinimumCadenceMinutes
                || document.CadenceMinutes > MaximumCadenceMinutes)
            {
                return null;
            }

            var legacyFilters = Normalize(document.Filters);
            return legacyFilters is null ? null : (legacyFilters, document.CadenceMinutes);
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
                // v2 record so it survives until the first successful search saves them.
                Persist(new FeedDocument(SchemaVersion, null, 0, enabled, null), changed: true);
                return;
            }

            if (existing.AutoFetchEnabled == enabled)
            {
                // A migrated v1 record is still only in memory; promote it now.
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
                feeds[index] = feed with
                {
                    LastFetchedUtc = now,
                    ConsecutiveFailures = failures,
                    BackoffUntilUtc = now + TimeSpan.FromSeconds(delaySeconds)
                };
            }

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
            if (respectCadence && feed.LastFetchedUtc.HasValue
                && now - feed.LastFetchedUtc.Value < TimeSpan.FromMinutes(feed.CadenceMinutes))
            {
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
    /// Reads the record, migrating a v1 record in memory. A migration is marked
    /// pending so the next write promotes the file to v2.
    /// </summary>
    private static FeedDocument? ReadCore()
    {
        try
        {
            var target = ResolvePath();
            if (!File.Exists(target))
                return null;
            if (new FileInfo(target).Length > 1024 * 1024)
                return null;

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

            if (document.Version != SchemaVersion)
            {
                // An unreadable record would otherwise be silently overwritten by the
                // next save, losing the user's Discover filters and opt-in for good.
                Quarantine();
                return null;
            }

            return document with { Feeds = NormalizeFeeds(document.Feeds) };
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

    /// <summary>Writes through when something changed, or when a v1 record needs promoting.</summary>
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

        // Validate() covers categories/purity, but an out-of-range enum from a
        // corrupt file would still throw inside WallhavenQueryBuilder at fetch time.
        if (!Enum.IsDefined(normalized.Sorting)
            || !Enum.IsDefined(normalized.Order)
            || !Enum.IsDefined(normalized.TopRange))
        {
            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Sanitizes a persisted feed list one entry at a time: a single bad feed is
    /// dropped instead of making the whole record unreadable.
    /// </summary>
    private static IReadOnlyList<RotationFeed>? NormalizeFeeds(IReadOnlyList<RotationFeed>? feeds)
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
                LastAppliedId = RotationFeed.IsSafeToken(
                    candidate.LastAppliedId, RotationFeed.MaximumAppliedIdLength, allowEmpty: true)
                    ? candidate.LastAppliedId
                    : null
            });

            if (normalized.Count == MaximumFeeds)
                break;
        }
        return normalized;
    }

    private sealed record FeedDocument(
        int Version,
        WallhavenFilters? Filters,
        int CadenceMinutes,
        bool AutoFetchEnabled,
        IReadOnlyList<RotationFeed>? Feeds);
}
