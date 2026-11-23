using System.Text.Json;

namespace Oriole.Core.Services;

public sealed record WallpaperLibraryEntry
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string PageUrl { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Purity { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public string Ratio { get; init; } = string.Empty;
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsFavorite { get; init; }
    public string Collection { get; init; } = "Unsorted";
}

/// <summary>
/// Small versioned JSON index for the local wallpaper library. Removing an entry never
/// deletes its image file; library metadata and user files are kept separate.
/// </summary>
public sealed class WallpaperLibraryStore
{
    private const int SchemaVersion = 1;
    private const long MaximumIndexBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _indexPath;

    public WallpaperLibraryStore(string? indexPath = null)
    {
        _indexPath = indexPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Oriole", "library", "library.json");
    }

    public async Task<IReadOnlyList<WallpaperLibraryEntry>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            return entries
                .OrderByDescending(entry => entry.SavedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task UpsertAsync(
        WallpaperLibraryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Id))
            throw new ArgumentException("A wallpaper ID is required.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.LocalPath) || !File.Exists(entry.LocalPath))
            throw new FileNotFoundException("The saved wallpaper file does not exist.", entry.LocalPath);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(existing =>
                string.Equals(existing.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var existing = entries[index];
                entries[index] = entry with
                {
                    IsFavorite = entry.IsFavorite || existing.IsFavorite,
                    Collection = string.Equals(entry.Collection, "Unsorted", StringComparison.Ordinal)
                        ? existing.Collection
                        : entry.Collection
                };
            }
            else
            {
                entries.Add(entry);
            }

            if (entries.Count > 5000)
                throw new InvalidOperationException("The wallpaper library reached its 5,000-item safety limit.");

            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> SetFavoriteAsync(
        string id,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(existing =>
                string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            entries[index] = entries[index] with { IsFavorite = isFavorite };
            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var removed = entries.RemoveAll(existing =>
                string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<WallpaperLibraryEntry>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
            return [];

        var info = new FileInfo(_indexPath);
        if (info.Length > MaximumIndexBytes)
            throw new InvalidDataException("The wallpaper library index exceeds the 8 MiB safety limit.");

        await using var stream = new FileStream(
            _indexPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<LibraryDocument>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);

        if (document is null)
            return [];
        if (document.Version != SchemaVersion)
            throw new InvalidDataException($"Unsupported wallpaper library version: {document.Version}.");

        return document.Entries ?? [];
    }

    private async Task WriteCoreAsync(
        List<WallpaperLibraryEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_indexPath)
            ?? throw new InvalidOperationException("The library index path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, new LibraryDocument(SchemaVersion, entries), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record LibraryDocument(int Version, List<WallpaperLibraryEntry>? Entries);
}
