using System.Text.Json;
using System.Text.Json.Serialization;
using Ori.Core.Models;

namespace Ori.Core.Services;

/// <summary>
/// Persists the Discover search preferences used by hands-free Wallhaven rotation.
/// The feed record is written atomically so a crash can never leave a half-written
/// preference file; a missing or corrupt record is simply reported as "no feed".
/// The opt-in flag lives in the same record so enabling auto-fetch survives restarts.
/// </summary>
public static class WallhavenFeedStore
{
    private const int SchemaVersion = 1;
    public const int MinimumCadenceMinutes = 1;
    public const int MaximumCadenceMinutes = 7 * 24 * 60; // one week

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "rotation-feed.json");

    private static readonly object SyncRoot = new();

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
            var document = new FeedDocument(
                SchemaVersion,
                filters,
                cadenceMinutes,
                existing?.AutoFetchEnabled ?? false);
            WriteCore(document);
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
            if (document is null
                || document.Version != SchemaVersion
                || document.CadenceMinutes < MinimumCadenceMinutes
                || document.CadenceMinutes > MaximumCadenceMinutes)
            {
                return null;
            }

            var filters = Normalize(document.Filters);
            return filters is null ? null : (filters, document.CadenceMinutes);
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
                // record so it survives until the first successful search saves them.
                WriteCore(new FeedDocument(SchemaVersion, null, 0, enabled));
                return;
            }

            if (existing.AutoFetchEnabled == enabled)
                return;

            WriteCore(existing with { AutoFetchEnabled = enabled });
        }
    }

    private static FeedDocument? ReadCore()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            if (new FileInfo(Path).Length > 1024 * 1024)
                return null;

            using var stream = new FileStream(
                Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<FeedDocument>(stream, JsonOptions);
            if (document is null || document.Version != SchemaVersion)
            {
                // An unreadable record would otherwise be silently overwritten by the
                // next save, losing the user's Discover filters and opt-in for good.
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

    /// <summary>Keeps a copy of an unreadable feed file so the next save cannot erase it.</summary>
    private static void Quarantine()
    {
        var backup = Path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        try
        {
            File.Move(Path, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing better is possible here; the next save will replace the file.
        }
    }

    private static void WriteCore(FeedDocument document)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
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

    private sealed record FeedDocument(
        int Version,
        WallhavenFilters? Filters,
        int CadenceMinutes,
        bool AutoFetchEnabled);
}
