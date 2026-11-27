using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ori.Desktop.Services;

/// <summary>
/// Small per-user UI preferences file. Kept separate from the library, schedule
/// and API-key stores so a corrupt or old preferences file can never prevent
/// the shell from starting.
/// </summary>
public sealed class UiPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public UiPreferencesStore()
        : this(null)
    {
    }

    /// <param name="dataDirectory">Optional Ori data folder override (used by tests).</param>
    public UiPreferencesStore(string? dataDirectory)
    {
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ori");
        FilePath = Path.Combine(root, "ui-preferences.json");
        StartMinimized = true;
    }

    /// <summary>Where the preferences are stored.</summary>
    [JsonIgnore]
    public string FilePath { get; }

    /// <summary>
    /// True by default: Ori starts with its window hidden and only the tray icon
    /// visible. Set to false to open the window normally at launch.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Reads the preferences. A missing, empty, corrupt or partial file is
    /// treated as a fresh install: all defaults are returned, never an exception.
    /// </summary>
    public static UiPreferencesStore Load(string? dataDirectory = null)
    {
        var store = new UiPreferencesStore(dataDirectory);
        try
        {
            if (!File.Exists(store.FilePath))
                return store;

            var json = File.ReadAllText(store.FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return store;

            var stored = JsonSerializer.Deserialize<StoredPreferences>(json, JsonOptions);
            if (stored is null)
                return store;

            return new UiPreferencesStore(dataDirectory) { StartMinimized = stored.StartMinimized };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            // Corrupt / unreadable preferences must never change startup behaviour
            // beyond falling back to the documented defaults.
            return store;
        }
    }

    /// <summary>
    /// Writes the preferences atomically: a fully written temporary file in the
    /// same folder replaces the old file in one move, so a crash mid-write can
    /// never leave a half JSON document behind.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(
                new StoredPreferences { StartMinimized = StartMinimized }, JsonOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    private sealed class StoredPreferences
    {
        public bool StartMinimized { get; set; } = true;
    }
}
