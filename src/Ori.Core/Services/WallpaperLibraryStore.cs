using System.Security.Cryptography;
using System.Text.Json;

namespace Ori.Core.Services;

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

    // Schema v2: provenance and discovery metadata. Older indexes are migrated to
    // empty strings so no entry is lost when the schema is upgraded.
    public string Tags { get; init; } = string.Empty;
    public string Colors { get; init; } = string.Empty;
    public string SourceQuery { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
}

/// <summary>
/// Small versioned JSON index for the local wallpaper library. Removing an entry never
/// deletes its image file unless explicitly requested; library metadata and user files
/// are otherwise kept separate.
/// </summary>
public sealed class WallpaperLibraryStore
{
    private const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private const int MaximumEntries = 5000;
    private const long MaximumIndexBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    // WindowsDesktopWallpaper cannot apply webp/gif, so importing them would create
    // library cards that can never be used as a wallpaper. Existing entries are kept.
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp"
    };

    private readonly string _indexPath;
    private readonly string _dataRoot;

    /// <param name="indexPath">Location of library.json. Tests may pass a temporary path.</param>
    /// <param name="dataDirectory">
    /// Root Ori owns. Image deletion and path repair are anchored here, never to the
    /// parent of an arbitrary index path, so a custom index cannot widen what is deletable.
    /// </param>
    public WallpaperLibraryStore(string? indexPath = null, string? dataDirectory = null)
    {
        var path = indexPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ori", "library", "library.json");
        // A bare filename has no parent directory; normalizing once keeps writes working
        // from any current working directory.
        _indexPath = Path.GetFullPath(path);
        _dataRoot = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ori"));
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

    /// <summary>
    /// Runs the same in-memory path repair as every read and commits it to disk when
    /// at least one stored path changed. Returns whether an index write happened.
    /// </summary>
    public async Task<bool> PersistRepairedPathsAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (entries, repaired) = await ReadCoreWithRepairInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!repaired)
                return false;

            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return true;
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
                // Re-saving a wallpaper the library already holds must not wipe what
                // the user attached to it. Discover re-applies an entry after a fresh
                // download, so tags, colours, the content hash and the original save
                // date are carried over unless the new value actually says something.
                var existing = entries[index];
                entries[index] = entry with
                {
                    IsFavorite = entry.IsFavorite || existing.IsFavorite,
                    Collection = string.Equals(entry.Collection, "Unsorted", StringComparison.Ordinal)
                        ? existing.Collection
                        : entry.Collection,
                    Tags = Coalesce(entry.Tags, existing.Tags),
                    Colors = Coalesce(entry.Colors, existing.Colors),
                    SourceQuery = Coalesce(entry.SourceQuery, existing.SourceQuery),
                    SourceId = Coalesce(entry.SourceId, existing.SourceId),
                    ContentHash = Coalesce(entry.ContentHash, existing.ContentHash),
                    SavedAtUtc = existing.SavedAtUtc
                };
            }
            else
            {
                entries.Add(entry);
            }

            if (entries.Count > MaximumEntries)
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

    public async Task<bool> SetCollectionAsync(
        string id,
        string collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(collection);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(existing =>
                string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            var target = string.IsNullOrWhiteSpace(collection) ? "Unsorted" : collection.Trim();
            entries[index] = entries[index] with { Collection = target };
            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> SetTagsAsync(
        string id,
        string tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = string.Join(", ", tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(existing =>
                string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            entries[index] = entries[index] with { Tags = normalized };
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

    /// <summary>
    /// Removes an entry and, when requested, its image file. Returns false only when the
    /// entry does not exist. Use <see cref="DeleteEntryWithResultAsync"/> when the caller
    /// needs to know whether the file was actually removed.
    /// </summary>
    public async Task<bool> DeleteEntryAsync(
        string id,
        bool deleteFile,
        CancellationToken cancellationToken = default)
    {
        var result = await DeleteEntryCoreAsync(id, deleteFile, cancellationToken).ConfigureAwait(false);
        return result.Removed;
    }

    /// <summary>
    /// Removes an entry and reports separately whether its image file was deleted. A file
    /// outside Ori's own folders is intentionally kept, and the UI should say so.
    /// </summary>
    public Task<(bool Removed, bool FileDeleted)> DeleteEntryWithResultAsync(
        string id,
        bool deleteFile,
        CancellationToken cancellationToken = default) =>
        DeleteEntryCoreAsync(id, deleteFile, cancellationToken);

    private async Task<(bool Removed, bool FileDeleted)> DeleteEntryCoreAsync(
        string id,
        bool deleteFile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(existing =>
                string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return (false, false);

            // Commit the index before touching the file. The other order leaves an
            // entry whose image is gone whenever the index write fails, and the UI can
            // never repair that. A leftover file is harmless: orphan cleanup finds it.
            entries.Remove(entry);
            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);

            var fileDeleted = deleteFile
                && !string.IsNullOrWhiteSpace(entry.LocalPath)
                && DeleteOwnedImage(entry.LocalPath);

            return (true, fileDeleted);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Deletes an image only when it lives under Ori's own library or Wallpapers folder.
    /// A wallpaper imported from a folder the user chose stays there, so removing the
    /// library entry must never delete the user's original file. Returns whether a file
    /// was actually removed.
    /// </summary>
    private bool DeleteOwnedImage(string storedPath)
    {
        string path;
        try
        {
            path = Path.GetFullPath(storedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // A malformed stored path must not make removal impossible.
        }

        var allowed = new[]
        {
            Path.Combine(_dataRoot, "library"),
            Path.Combine(_dataRoot, "Wallpapers")
        };
        if (!allowed.Any(directory =>
                IsInsideRoot(path, Path.GetFullPath(directory), Path.GetFullPath(directory) + Path.DirectorySeparatorChar)))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // A locked image is left on disk; the entry is already gone.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Coalesce(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    /// <summary>
    /// Moves an unreadable index aside so the next visit starts clean instead of
    /// failing forever. A GUID suffix avoids a same-second name collision. Returns the
    /// backup path, or null when the move itself fails.
    /// </summary>
    private string? QuarantineIndex()
    {
        var backup = _indexPath + ".corrupt-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Move(_indexPath, backup, overwrite: false);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively imports jpg/jpeg/png/bmp files, hashing each one with SHA-256
    /// and skipping files whose hash already exists in the library (or in this batch).
    /// Reparse points are never traversed, so a symlink/junction cannot escape the folder.
    /// Returns the number of entries added.
    /// </summary>
    public async Task<int> ImportFolderAsync(
        string folderPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The folder '{folderPath}' does not exist.");

        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        // Hashing happens before the lock so large folders do not block other callers.
        var candidates = new List<WallpaperLibraryEntry>();
        var hashesInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateImageFilesSafely(root, rootWithSeparator, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Hashing {Path.GetFileName(file)}…");

            string hash;
            try
            {
                await using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException)
            {
                progress?.Report($"Skipped locked file {Path.GetFileName(file)}.");
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!hashesInBatch.Add(hash))
                continue;

            candidates.Add(new WallpaperLibraryEntry
            {
                Id = "local-" + hash.ToLowerInvariant()[..24],
                Title = Path.GetFileNameWithoutExtension(file),
                LocalPath = file,
                ContentHash = hash,
                Category = "Local",
                Collection = "Unsorted"
            });
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var knownHashes = new HashSet<string>(
                entries.Where(entry => !string.IsNullOrWhiteSpace(entry.ContentHash))
                    .Select(entry => entry.ContentHash),
                StringComparer.OrdinalIgnoreCase);
            var knownIds = new HashSet<string>(entries.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);

            var imported = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!knownHashes.Add(candidate.ContentHash))
                    continue;
                if (entries.Count + imported >= MaximumEntries)
                {
                    // Commit what already fits instead of throwing away the whole
                    // batch. The caller reports how many were imported so the user
                    // knows the remainder was skipped at the safety cap.
                    break;
                }

                var id = knownIds.Add(candidate.Id) ? candidate.Id : "local-" + Guid.NewGuid().ToString("N");
                entries.Add(candidate with { Id = id });
                imported++;
            }

            if (imported > 0)
                await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return imported;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Returns the redundant copies (every entry after the first in each duplicate
    /// group) as (Id, LocalPath) pairs. Entries are grouped by ContentHash, with a
    /// normalized-path-plus-size fallback for older entries without a hash.
    /// </summary>
    public async Task<List<(string Id, string Path)>> FindDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var byHash = new Dictionary<string, List<WallpaperLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            var unhashed = new List<WallpaperLibraryEntry>();
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.ContentHash))
                {
                    if (!byHash.TryGetValue(entry.ContentHash, out var group))
                        byHash[entry.ContentHash] = group = [];
                    group.Add(entry);
                }
                else
                {
                    unhashed.Add(entry);
                }
            }

            var duplicates = new List<(string Id, string Path)>();
            foreach (var group in byHash.Values.Where(group => group.Count > 1))
                duplicates.AddRange(group.Skip(1).Select(entry => (entry.Id, entry.LocalPath)));

            var byFallback = new Dictionary<string, List<WallpaperLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in unhashed)
            {
                var key = BuildFallbackKey(entry);
                if (key is null)
                    continue;
                if (!byFallback.TryGetValue(key, out var group))
                    byFallback[key] = group = [];
                group.Add(entry);
            }
            foreach (var group in byFallback.Values.Where(group => group.Count > 1))
                duplicates.AddRange(group.Skip(1).Select(entry => (entry.Id, entry.LocalPath)));

            return duplicates;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IEnumerable<string> EnumerateImageFilesSafely(
        string root,
        string rootWithSeparator,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = [.. Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)];
            }
            catch (UnauthorizedAccessException)
            {
                files = [];
            }

            foreach (var file in files)
            {
                if (IsReparsePoint(file))
                    continue;
                var fullPath = Path.GetFullPath(file);
                if (!IsInsideRoot(fullPath, root, rootWithSeparator))
                    continue;
                if (ImportableExtensions.Contains(Path.GetExtension(fullPath)))
                    yield return fullPath;
            }

            string[] children;
            try
            {
                children = [.. Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)];
            }
            catch (UnauthorizedAccessException)
            {
                children = [];
            }

            foreach (var child in children)
            {
                // Never traverse a reparse point: a junction or symlink could lead
                // outside the folder the user chose to import.
                if (IsReparsePoint(child))
                    continue;
                var fullPath = Path.GetFullPath(child);
                if (!IsInsideRoot(fullPath, root, rootWithSeparator))
                    continue;
                pending.Push(fullPath);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsInsideRoot(string candidate, string root, string rootWithSeparator) =>
        string.Equals(candidate, root, PathComparison)
        || candidate.StartsWith(rootWithSeparator, PathComparison);

    private static string? BuildFallbackKey(WallpaperLibraryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.LocalPath))
            return null;

        string path;
        try
        {
            path = Path.GetFullPath(entry.LocalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        long size = -1;
        try
        {
            if (File.Exists(path))
                size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // Keep the size unknown; the normalized path still participates.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return $"{path}\u001f{size}";
    }

    private async Task<List<WallpaperLibraryEntry>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var (entries, _) = await ReadCoreWithRepairInfoAsync(cancellationToken).ConfigureAwait(false);
        return entries;
    }

    private async Task<(List<WallpaperLibraryEntry> Entries, bool Repaired)> ReadCoreWithRepairInfoAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
            return ([], false);

        var info = new FileInfo(_indexPath);
        if (info.Length > MaximumIndexBytes)
            throw new InvalidDataException("The wallpaper library index exceeds the 8 MiB safety limit.");

        LibraryDocument? document;
        try
        {
            await using var stream = new FileStream(
                _indexPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer.DeserializeAsync<LibraryDocument>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A damaged index used to break the Library page on every visit with no
            // way out short of deleting the file. Move it aside, keep a backup and
            // start empty; the images themselves are never touched.
            var backup = QuarantineIndex();
            var detail = backup is null
                ? "The damaged file could not be moved aside; close other copies of Ori and try again."
                : $"A copy is at {backup} and the library starts empty; saved images are untouched.";
            throw new JsonException($"The wallpaper library index was damaged. {detail}");
        }

        if (document is null)
        {
            QuarantineIndex();
            return ([], false);
        }

        if (document.Version != LegacySchemaVersion && document.Version != SchemaVersion)
        {
            var backup = QuarantineIndex();
            var detail = backup is null
                ? "The file could not be moved aside; close other copies of Ori and try again."
                : $"A copy is at {backup} and the library starts empty; saved images are untouched.";
            throw new InvalidDataException(
                $"Unsupported wallpaper library version: {document.Version}. {detail}");
        }

        // Version-1 entries simply lacked the new fields. Applying the normalizer to
        // both versions fills safe defaults and keeps every existing entry intact.
        var entries = (document.Entries ?? []).Select(NormalizeForCurrentSchema).ToList();
        var repaired = RepairLocalPaths(entries);
        // RepairLocalPaths returns the original list when nothing changed.
        return (repaired, !ReferenceEquals(repaired, entries));
    }

    /// <summary>
    /// Rebases library paths onto the current data folder when a stored absolute
    /// path no longer exists. Renaming or restoring %LOCALAPPDATA%\Ori otherwise
    /// leaves every entry pointing at a folder that is gone, so the library shows
    /// empty cards and rotation reports that no images are available.
    /// </summary>
    /// <remarks>
    /// Only an entry whose file name is present in the current images folder is
    /// rebased, so wallpapers imported from a folder the user chose are never
    /// touched, and an entry the user deleted stays missing instead of silently
    /// pointing at a different file.
    /// </remarks>
    private List<WallpaperLibraryEntry> RepairLocalPaths(List<WallpaperLibraryEntry> entries)
    {
        if (entries.Count == 0)
            return entries;

        var indexDirectory = Path.GetDirectoryName(_indexPath) ?? ".";
        var imagesDirectory = Path.Combine(indexDirectory, "images");
        var imagesPrefix = imagesDirectory + Path.DirectorySeparatorChar;
        List<WallpaperLibraryEntry>? repaired = null;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (string.IsNullOrWhiteSpace(entry.LocalPath))
                continue;
            if (entry.LocalPath.StartsWith(@"\\", StringComparison.Ordinal))
                continue; // A network share may come back; don't pin it to a local copy.
            if (entry.LocalPath.StartsWith(imagesPrefix, PathComparison))
                continue; // already inside the current library folder
            if (File.Exists(entry.LocalPath))
                continue; // imported from somewhere the user chose

            var name = Path.GetFileName(entry.LocalPath);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Keep the sub-folder the file lived in, so a wallpaper applied into
            // "Wallpapers" is not silently re-pointed at a same-named copy in
            // "library\images", which would then look like an orphan to cleanup.
            var storedParent = Path.GetFileName(
                Path.GetDirectoryName(entry.LocalPath) ?? string.Empty);
            string? candidate = null;
            if (!string.IsNullOrWhiteSpace(storedParent))
            {
                candidate = Existing(Path.Combine(indexDirectory, storedParent, name));
                if (candidate is null)
                    candidate = Existing(Path.Combine(_dataRoot, storedParent, name));
            }
            candidate ??= Existing(Path.Combine(imagesDirectory, name));
            if (candidate is null || !MatchesHash(entry, candidate))
                continue;

            repaired ??= new List<WallpaperLibraryEntry>(entries);
            repaired[index] = entry with { LocalPath = candidate };
        }

        return repaired ?? entries;

        static string? Existing(string path) => File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Computes the same uppercase SHA-256 the import path uses, or null when the file
    /// cannot be read (locked, missing, malformed path). Never throws for those cases.
    /// </summary>
    public static string? ComputeSha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A file name alone is not proof of identity: another wallpaper can share it. Only
    /// rebase onto a candidate whose SHA-256 matches the recorded hash. Older entries
    /// have no hash, so they keep the filename-based behaviour.
    /// </summary>
    private static bool MatchesHash(WallpaperLibraryEntry entry, string candidate)
    {
        if (string.IsNullOrWhiteSpace(entry.ContentHash))
            return true;

        try
        {
            using var stream = new FileStream(
                candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, entry.ContentHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Fills safe defaults for fields that did not exist in older schemas.</summary>
    private static WallpaperLibraryEntry NormalizeForCurrentSchema(WallpaperLibraryEntry entry) => entry with
    {
        Title = Safe(entry.Title),
        PageUrl = Safe(entry.PageUrl),
        ImageUrl = Safe(entry.ImageUrl),
        LocalPath = Safe(entry.LocalPath),
        Category = Safe(entry.Category),
        Purity = Safe(entry.Purity),
        Ratio = Safe(entry.Ratio),
        Collection = string.IsNullOrWhiteSpace(entry.Collection) ? "Unsorted" : entry.Collection,
        Tags = Safe(entry.Tags),
        Colors = Safe(entry.Colors),
        SourceQuery = Safe(entry.SourceQuery),
        SourceId = Safe(entry.SourceId),
        ContentHash = Safe(entry.ContentHash)
    };

    private static string Safe(string? value) => value ?? string.Empty;

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
