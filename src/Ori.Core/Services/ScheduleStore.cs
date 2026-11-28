using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ori.Core.Services;

/// <summary>Where a display's rotation candidates come from.</summary>
/// <remarks>
/// <see cref="RotationSourceKind.Library"/> = every available library entry;
/// <see cref="RotationSourceKind.Collection"/> = entries in one named collection
/// (<see cref="DisplayScheduleState.SourceId"/>); <see cref="RotationSourceKind.Feed"/>
/// = entries tagged <c>feed:&lt;id&gt;</c>. <see cref="RotationSourceKind.Library"/> is
/// the default so v1 schedule files and older callers stay valid.
/// </remarks>
public enum RotationSourceKind
{
    Library,
    Collection,
    Feed
}

/// <summary>Persisted state for one display's rotation schedule.</summary>
/// <remarks>
/// MonitorId is the volatile COM device path. MonitorFingerprint (device name +
/// geometry) is the stable identity used to adopt a saved schedule after a driver,
/// USB or GPU change gives the same panel a new path. Trailing properties have
/// defaults so older six-argument callers (and older saved files) stay valid.
///
/// Schema v2 adds per-display source selection. <see cref="SourceKind"/> defaults
/// to <see cref="RotationSourceKind.Library"/> and <see cref="SourceId"/> to null,
/// so v1 files migrate transparently.
/// </remarks>
public sealed record DisplayScheduleState(
    string MonitorId,
    string IntervalName,
    bool ShouldRun,
    bool UserPaused,
    DateTimeOffset? NextRunUtc,
    string? LastAppliedId,
    bool PauseWhenFullscreen = true,
    string? MonitorFingerprint = null,
    string? PreviousAppliedPath = null,
    RotationSourceKind SourceKind = RotationSourceKind.Library,
    string? SourceId = null);

/// <summary>
/// Small versioned JSON store for per-display schedules. A corrupt or unsupported
/// index is treated as "no saved schedules" so a bad file cannot block startup.
/// </summary>
/// <remarks>
/// Schema v2 is written. v1 files are read and migrated: missing
/// <see cref="DisplayScheduleState.SourceKind"/>/<see cref="DisplayScheduleState.SourceId"/>
/// default to <see cref="RotationSourceKind.Library"/>/null. Unknown extra fields are
/// tolerated; an unrecognised schema version is quarantined.
/// </remarks>
public sealed class ScheduleStore
{
    /// <summary>Current on-disk schema version (v2 written; v1 read).</summary>
    public const int SchemaVersion = 2;

    /// <summary>Lowest rotation interval accepted, in whole minutes.</summary>
    public const int MinimumIntervalMinutes = 1;

    /// <summary>Highest rotation interval accepted, in whole minutes (7 days).</summary>
    public const int MaximumIntervalMinutes = 10080;

    private const long MaximumIndexBytes = 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Tolerate unknown fields and unknown enum values a newer writer might add.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new TolerantEnumConverterFactory() }
    };
    private readonly string _path;

    /// <summary>Legacy human-readable names that must keep parsing.</summary>
    private static readonly (string Name, int Minutes)[] LegacyIntervals =
    [
        ("1 minute", 1),
        ("5 minutes", 5),
        ("10 minutes", 10),
        ("15 minutes", 15),
        ("30 minutes", 30),
        ("1 hour", 60),
        ("2 hours", 120),
        ("6 hours", 360),
        ("12 hours", 720),
        ("1 day", 1440),
        ("2 days", 2880),
        ("3 days", 4320),
        ("7 days", 10080)
    ];

    private static readonly Regex MinutesPattern = new(
        @"^(\d+)\s*(?:minutes?|mins?|m)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HoursPattern = new(
        @"^(\d+)\s*hours?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DaysPattern = new(
        @"^(\d+)\s*days?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The built-in interval names shown in the picker; custom values are appended per display.</summary>
    public static IReadOnlyList<string> LegacyIntervalNames { get; } =
        LegacyIntervals.Select(entry => entry.Name).ToArray();

    public ScheduleStore(string? path = null)
    {
        var target = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ori", "schedules", "schedules.json");
        _path = Path.GetFullPath(target);
    }

    /// <summary>
    /// Builds a stable identity from the monitor's friendly name and geometry. Unlike the
    /// COM device path this survives re-enumeration, so a saved schedule can be matched
    /// back to the same physical panel after a driver/USB/GPU change.
    /// </summary>
    public static string BuildFingerprint(
        string? deviceName,
        int left,
        int top,
        int width,
        int height) =>
        $"{(deviceName ?? string.Empty).Trim()}|{left},{top},{width}x{height}";

    /// <summary>
    /// Parses an <see cref="DisplayScheduleState.IntervalName"/> into a whole-minute
    /// count in [<see cref="MinimumIntervalMinutes"/>, <see cref="MaximumIntervalMinutes"/>].
    /// Legacy names ("1 minute", "1 hour", "7 days", ...) still parse; any arbitrary
    /// whole-minute value is accepted as a bare number, "N", "N minutes", "N mins",
    /// "N hours" or "N days". Returns false for blank, non-numeric or out-of-range input.
    /// </summary>
    public static bool TryParseIntervalName(string? name, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();

        foreach (var (legacyName, legacyMinutes) in LegacyIntervals)
        {
            if (string.Equals(trimmed, legacyName, StringComparison.OrdinalIgnoreCase))
            {
                minutes = legacyMinutes;
                return true;
            }
        }

        if (TryMatchMinutes(MinutesPattern, trimmed, 1, out minutes))
            return true;
        if (TryMatchMinutes(HoursPattern, trimmed, 60, out minutes))
            return true;
        if (TryMatchMinutes(DaysPattern, trimmed, 1440, out minutes))
            return true;

        return false;
    }

    private static bool TryMatchMinutes(Regex pattern, string value, int multiplier, out int minutes)
    {
        minutes = 0;
        var match = pattern.Match(value);
        if (!match.Success)
            return false;
        if (!int.TryParse(match.Groups[1].Value, out var count) || count < 0)
            return false;

        // Guard against overflow before multiplying.
        if (count > MaximumIntervalMinutes)
            return false;

        var total = count * multiplier;
        if (total < MinimumIntervalMinutes || total > MaximumIntervalMinutes)
            return false;

        minutes = total;
        return true;
    }

    /// <summary>
    /// Canonical interval string for <paramref name="minutes"/>. A legacy name is
    /// preferred when one matches; otherwise "N minutes" (N == 1 -> "1 minute").
    /// </summary>
    public static string FormatIntervalName(int minutes)
    {
        var clamped = Math.Clamp(minutes, MinimumIntervalMinutes, MaximumIntervalMinutes);
        foreach (var (legacyName, legacyMinutes) in LegacyIntervals)
        {
            if (legacyMinutes == clamped)
                return legacyName;
        }
        return clamped == 1 ? "1 minute" : $"{clamped} minutes";
    }

    public async Task<IReadOnlyDictionary<string, DisplayScheduleState>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return Empty();

            if (new FileInfo(_path).Length > MaximumIndexBytes)
            {
                // Without a backup the next save would silently replace the file and
                // destroy every schedule it contained.
                Quarantine();
                return Empty();
            }

            ScheduleDocument? document;
            try
            {
                await using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                document = await JsonSerializer.DeserializeAsync<ScheduleDocument>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Saving over a file we cannot read would destroy the schedules for
                // good, so keep a copy and start clean instead.
                Quarantine();
                return Empty();
            }

            // v1 (no schema bump) and v2 are both accepted; anything else is unsupported.
            if (document is null || document.Entries is null || !IsSupportedVersion(document.Version))
            {
                Quarantine();
                return Empty();
            }

            var stored = document.Entries ?? [];
            var entries = stored
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.MonitorId))
                .Select(NormalizeEntry)
                .ToArray();
            if (entries.Length != stored.Length)
                Quarantine();

            return entries
                .GroupBy(entry => entry.MonitorId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.First().MonitorId, group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Empty();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<DisplayScheduleState> entries,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var normalized = entries.Select(NormalizeEntry).ToArray();
                    await JsonSerializer.SerializeAsync(
                        stream, new ScheduleDocument(SchemaVersion, normalized),
                        JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>True for the current schema and the v1 files we migrate on read.</summary>
    private static bool IsSupportedVersion(int version) => version is 1 or SchemaVersion;

    /// <summary>
    /// Applies v1->v2 migration defaults and clamps an unrecognised source kind back to
    /// <see cref="RotationSourceKind.Library"/>. Unknown JSON fields are already ignored.
    /// </summary>
    private static DisplayScheduleState NormalizeEntry(DisplayScheduleState entry)
    {
        var kind = Enum.IsDefined(entry.SourceKind) ? entry.SourceKind : RotationSourceKind.Library;
        var sourceId = string.IsNullOrWhiteSpace(entry.SourceId) ? null : entry.SourceId.Trim();
        return entry with { SourceKind = kind, SourceId = sourceId };
    }

    private static Dictionary<string, DisplayScheduleState> Empty() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps a copy of an unreadable schedule file so the next save cannot erase it.</summary>
    private void Quarantine()
    {
        var backup = _path + ".corrupt-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Move(_path, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing better is possible here; the next save will replace the file.
        }
    }

    private sealed record ScheduleDocument(int Version, DisplayScheduleState[]? Entries);
}
