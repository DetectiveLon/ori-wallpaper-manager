using System.Text.Json;
using System.Text.Json.Serialization;
using Ori.Core.Services;

namespace Ori.Desktop.Services;

/// <summary>
/// The five image-fitting behaviours Windows exposes per wallpaper. Span is
/// deliberately NOT a member: it is a separate, explicit desktop-wide toggle.
/// </summary>
public enum DisplayFit
{
    Fill,
    Fit,
    Stretch,
    Center,
    Tile
}

/// <summary>One display's remembered choices, keyed by a stable panel fingerprint.</summary>
public sealed record DisplayPreference(
    DisplayFit Fit = DisplayFit.Fill,
    string? ImagePath = null,
    string? SourceKind = null,
    string? SourceId = null,
    DateTimeOffset? SavedAtUtc = null);

/// <summary>
/// Persists per-display fit/source choices and the Span state under
/// %LOCALAPPDATA%\Ori\settings\display-preferences.json.
///
/// Writes are atomic (temporary file in the same directory, then replace) and an
/// unreadable file is moved to a .corrupt-* name instead of being overwritten.
/// Nothing here throws at callers: a broken preferences file must never stop the
/// Displays page from opening.
/// </summary>
public sealed class DisplayPreferencesStore
{
    private const int SchemaVersion = 1;
    private const long MaximumFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new TolerantEnumConverterFactory() }
    };

    private readonly Dictionary<string, DisplayPreference> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public DisplayPreferencesStore()
        : this(null)
    {
    }

    /// <param name="dataDirectory">Optional Ori data folder override (used by tests).</param>
    public DisplayPreferencesStore(string? dataDirectory)
    {
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ori");
        FilePath = Path.Combine(root, "settings", "display-preferences.json");
    }

    /// <summary>Where the preferences are stored.</summary>
    [JsonIgnore]
    public string FilePath { get; }

    /// <summary>The display currently targeted by the page, by fingerprint.</summary>
    public string? SelectedFingerprint { get; private set; }

    /// <summary>True while Windows is using one image across the whole virtual desktop.</summary>
    public bool SpanActive { get; private set; }

    /// <summary>The fit to restore when the user leaves Span mode.</summary>
    public DisplayFit SpanPreviousFit { get; private set; } = DisplayFit.Fill;

    /// <summary>The fingerprint whose fit was active before Span was enabled.</summary>
    public string? SpanPreviousFingerprint { get; private set; }

    /// <summary>The one image used while Span is active.</summary>
    public string? SpanImagePath { get; private set; }

    /// <summary>All saved per-display choices. Keys are panel fingerprints, never COM indices.</summary>
    public IReadOnlyDictionary<string, DisplayPreference> Entries => _entries;

    /// <summary>Reads the store, quarantining a corrupt or unsupported file. Never throws.</summary>
    public static DisplayPreferencesStore Load(string? dataDirectory = null)
    {
        var store = new DisplayPreferencesStore(dataDirectory);
        try
        {
            if (!File.Exists(store.FilePath))
                return store;

            if (new FileInfo(store.FilePath).Length > MaximumFileBytes)
            {
                store.Quarantine();
                return store;
            }

            var json = File.ReadAllText(store.FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return store;

            var document = JsonSerializer.Deserialize<PreferencesDocument>(json, JsonOptions);
            if (document is null || document.Version != SchemaVersion || document.Displays is null)
            {
                store.Quarantine();
                return store;
            }

            foreach (var entry in document.Displays)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Fingerprint))
                    continue;

                store._entries[entry.Fingerprint.Trim()] = new DisplayPreference(
                    entry.Fit,
                    NullIfBlank(entry.ImagePath),
                    NullIfBlank(entry.SourceKind),
                    NullIfBlank(entry.SourceId),
                    entry.SavedAtUtc);
            }

            store.SelectedFingerprint = NullIfBlank(document.SelectedFingerprint);
            store.SpanActive = document.SpanActive;
            store.SpanPreviousFit = Normalize(document.SpanPreviousFit);
            store.SpanPreviousFingerprint = NullIfBlank(document.SpanPreviousFingerprint);
            store.SpanImagePath = NullIfBlank(document.SpanImagePath);
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException or ArgumentException)
        {
            // The stream is closed before Quarantine moves the file on Windows.
            store.Quarantine();
            return new DisplayPreferencesStore(dataDirectory);
        }
    }

    /// <summary>Returns Fill for a display with no saved choice.</summary>
    public DisplayFit GetFit(string? fingerprint) =>
        fingerprint is not null && _entries.TryGetValue(fingerprint, out var entry)
            ? Normalize(entry.Fit)
            : DisplayFit.Fill;

    /// <summary>Returns the last image chosen for this panel, if it was saved.</summary>
    public string? GetImagePath(string? fingerprint) =>
        fingerprint is not null && _entries.TryGetValue(fingerprint, out var entry)
            ? NullIfBlank(entry.ImagePath)
            : null;

    /// <summary>Returns the last source kind/id chosen for this panel.</summary>
    public (string? Kind, string? Id) GetSource(string? fingerprint) =>
        fingerprint is not null && _entries.TryGetValue(fingerprint, out var entry)
            ? (NullIfBlank(entry.SourceKind), NullIfBlank(entry.SourceId))
            : (null, null);

    public void SetFit(string? fingerprint, DisplayFit fit)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return;

        var key = fingerprint.Trim();
        var previous = _entries.TryGetValue(key, out var existing)
            ? existing
            : new DisplayPreference();
        _entries[key] = previous with { Fit = Normalize(fit), SavedAtUtc = DateTimeOffset.UtcNow };
    }

    public void SetImage(string? fingerprint, string? imagePath, string? sourceKind, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return;

        var key = fingerprint.Trim();
        var previous = _entries.TryGetValue(key, out var existing)
            ? existing
            : new DisplayPreference();
        _entries[key] = previous with
        {
            ImagePath = NullIfBlank(imagePath),
            SourceKind = NullIfBlank(sourceKind),
            SourceId = NullIfBlank(sourceId),
            SavedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public void SetSelection(string? fingerprint) => SelectedFingerprint = NullIfBlank(fingerprint);

    /// <summary>Records the fit to restore when Span is switched off.</summary>
    public void SetSpan(bool active, DisplayFit previousFit, string? previousFingerprint, string? imagePath)
    {
        SpanActive = active;
        SpanPreviousFit = Normalize(previousFit);
        SpanPreviousFingerprint = NullIfBlank(previousFingerprint);
        SpanImagePath = NullIfBlank(imagePath);
    }

    /// <summary>
    /// Atomically saves all choices. A failed write leaves the previous file
    /// intact; callers can keep working from the in-memory values.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = new PreferencesDocument(
                SchemaVersion,
                _entries.Select(pair => new StoredDisplay(
                    pair.Key,
                    pair.Value.Fit,
                    pair.Value.ImagePath,
                    pair.Value.SourceKind,
                    pair.Value.SourceId,
                    pair.Value.SavedAtUtc)).ToArray(),
                SelectedFingerprint,
                SpanActive,
                SpanPreviousFit,
                SpanPreviousFingerprint,
                SpanImagePath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory choices still work for this session.
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

    private static DisplayFit Normalize(DisplayFit fit) =>
        Enum.IsDefined(fit) ? fit : DisplayFit.Fill;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Keeps a copy of an unreadable file so the next save cannot erase it.</summary>
    private void Quarantine()
    {
        if (!File.Exists(FilePath))
            return;

        var backup = FilePath + ".corrupt-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Move(FilePath, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing better is possible here; the next save will replace the file.
        }
    }

    private sealed record PreferencesDocument(
        int Version,
        StoredDisplay[]? Displays,
        string? SelectedFingerprint,
        bool SpanActive,
        DisplayFit SpanPreviousFit = DisplayFit.Fill,
        string? SpanPreviousFingerprint = null,
        string? SpanImagePath = null);

    private sealed record StoredDisplay(
        string Fingerprint,
        DisplayFit Fit,
        string? ImagePath,
        string? SourceKind,
        string? SourceId,
        DateTimeOffset? SavedAtUtc);
}
