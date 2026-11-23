using System.Text.Json;

namespace Oriole.Desktop.Services;

/// <summary>
/// Persists the most recently used Wallhaven tag queries as a small JSON file under
/// %LOCALAPPDATA%\Oriole. Corrupt or unreadable files degrade to an empty list; writes
/// are atomic (temporary file plus replace) so a crash cannot leave a half-written list.
/// </summary>
public static class RecentTagsStore
{
    public const int MaximumTags = 12;
    private const int MaximumFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Oriole", "settings", "recent-tags.json");

    /// <summary>Loads recent tags, newest first. Never throws.</summary>
    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return Array.Empty<string>();
            if (new FileInfo(StorePath).Length > MaximumFileBytes)
                return Array.Empty<string>();

            var json = File.ReadAllText(StorePath);
            var values = JsonSerializer.Deserialize<string[]>(json, JsonOptions);
            if (values is null)
                return Array.Empty<string>();

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumTags)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Atomically saves recent tags, capped at <see cref="MaximumTags"/>.</summary>
    public static void Save(IEnumerable<string> tags)
    {
        var values = (tags ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTags)
            .ToArray();

        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(values, JsonOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, StorePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
