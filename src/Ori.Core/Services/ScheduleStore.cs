using System.Text.Json;

namespace Ori.Core.Services;

/// <summary>Persisted state for one display's rotation schedule.</summary>
/// <remarks>
/// MonitorId is the volatile COM device path. MonitorFingerprint (device name +
/// geometry) is the stable identity used to adopt a saved schedule after a driver,
/// USB or GPU change gives the same panel a new path. The two trailing properties
/// have defaults so older six-argument callers (and older saved files) stay valid.
/// </remarks>
public sealed record DisplayScheduleState(
    string MonitorId,
    string IntervalName,
    bool ShouldRun,
    bool UserPaused,
    DateTimeOffset? NextRunUtc,
    string? LastAppliedId,
    bool PauseWhenFullscreen = true,
    string? MonitorFingerprint = null);

/// <summary>
/// Small versioned JSON store for per-display schedules. A corrupt or unsupported
/// index is treated as "no saved schedules" so a bad file cannot block startup.
/// </summary>
public sealed class ScheduleStore
{
    private const int SchemaVersion = 1;
    private const long MaximumIndexBytes = 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

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

    public async Task<IReadOnlyDictionary<string, DisplayScheduleState>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return Empty();

            if (new FileInfo(_path).Length > MaximumIndexBytes)
                return Empty();

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

            if (document is null || document.Version != SchemaVersion || document.Entries is null)
            {
                if (document is null || document.Version != SchemaVersion)
                    Quarantine();
                return Empty();
            }

            return document.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.MonitorId))
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
                    await JsonSerializer.SerializeAsync(
                        stream, new ScheduleDocument(SchemaVersion, entries.ToArray()),
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

    private static Dictionary<string, DisplayScheduleState> Empty() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps a copy of an unreadable schedule file so the next save cannot erase it.</summary>
    private void Quarantine()
    {
        var backup = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
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
