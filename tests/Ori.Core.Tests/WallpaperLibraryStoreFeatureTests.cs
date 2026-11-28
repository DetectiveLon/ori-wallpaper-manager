using System.Security.Cryptography;
using System.Text.Json;
using Ori.Core.Services;

namespace Ori.Core.Tests;

public sealed class WallpaperLibraryStoreFeatureTests
{
    [Fact]
    public async Task VersionOneIndexMigratesWithSafeDefaultsAndNoEntryLoss()
    {
        using var temp = new TemporaryDirectory();
        var imagePath = WriteFile(temp.Path, "legacy.jpg", [0x01, 0x02, 0x03]);
        var index = Path.Combine(temp.Path, "library.json");
        var legacy = new
        {
            Version = 1,
            Entries = new[]
            {
                new
                {
                    Id = "legacy-1",
                    Title = "Legacy wallpaper",
                    LocalPath = imagePath,
                    Category = "anime",
                    Purity = "sfw",
                    Width = 1920,
                    Height = 1080
                }
            }
        };
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(legacy));

        var entries = await new WallpaperLibraryStore(index).GetAllAsync();

        var entry = Assert.Single(entries);
        Assert.Equal("legacy-1", entry.Id);
        Assert.Equal("Legacy wallpaper", entry.Title);
        Assert.Equal(imagePath, entry.LocalPath);
        Assert.Equal("anime", entry.Category);
        Assert.Equal(1920, entry.Width);
        Assert.Equal(string.Empty, entry.Tags);
        Assert.Equal(string.Empty, entry.Colors);
        Assert.Equal(string.Empty, entry.SourceQuery);
        Assert.Equal(string.Empty, entry.SourceId);
        Assert.Equal(string.Empty, entry.ContentHash);
        Assert.Equal("Unsorted", entry.Collection);
        Assert.Equal(DateTimeOffset.MinValue, entry.SavedAtUtc);

        // Missing save dates must stay stable across reads instead of taking UtcNow.
        Assert.Equal(entry, Assert.Single(await new WallpaperLibraryStore(index).GetAllAsync()));
    }

    [Fact]
    public async Task MigrationSurvivesASubsequentWrite()
    {
        using var temp = new TemporaryDirectory();
        var imagePath = WriteFile(temp.Path, "legacy.jpg", [0x01, 0x02, 0x03]);
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[] { new { Id = "legacy-1", Title = "Legacy", LocalPath = imagePath } }
        }));

        var store = new WallpaperLibraryStore(index);
        Assert.True(await store.SetTagsAsync("legacy-1", "nature, landscape"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(index));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        var stored = Assert.Single(await store.GetAllAsync());
        Assert.Equal("nature, landscape", stored.Tags);
    }

    [Fact]
    public async Task ImportFolderHashesFilesAndSkipsDuplicateContent()
    {
        using var temp = new TemporaryDirectory();
        var folder = Path.Combine(temp.Path, "pictures");
        var nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        byte[] duplicateContent = [0x10, 0x20, 0x30, 0x40];
        WriteFile(folder, "a.jpg", duplicateContent);
        WriteFile(nested, "b.jpeg", duplicateContent);
        WriteFile(folder, "c.png", [0x50, 0x60, 0x70]);
        WriteFile(folder, "ignored.txt", duplicateContent);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        var imported = await store.ImportFolderAsync(folder, progress: null, CancellationToken.None);

        Assert.Equal(2, imported);
        var entries = await store.GetAllAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ContentHash)));
        Assert.DoesNotContain(entries, entry => entry.LocalPath.EndsWith("ignored.txt", StringComparison.Ordinal));

        var secondRun = await store.ImportFolderAsync(folder, progress: null, CancellationToken.None);
        Assert.Equal(0, secondRun);
        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task ImportFolderRejectsMissingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            store.ImportFolderAsync(Path.Combine(temp.Path, "does-not-exist"), null, default));
    }

    [Fact]
    public async Task FindDuplicatesGroupsByIdenticalContentHash()
    {
        using var temp = new TemporaryDirectory();
        var first = WriteFile(temp.Path, "first.jpg", [0x01, 0x02]);
        var second = WriteFile(temp.Path, "second.jpg", [0x03, 0x04]);
        var third = WriteFile(temp.Path, "third.jpg", [0x05, 0x06]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "one", LocalPath = first, ContentHash = "SAMEHASH" });
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "two", LocalPath = second, ContentHash = "SAMEHASH" });
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "three", LocalPath = third, ContentHash = "DIFFERENT" });

        var duplicates = await store.FindDuplicatesAsync();

        var duplicate = Assert.Single(duplicates);
        Assert.Equal("two", duplicate.Id);
        Assert.Equal(second, duplicate.Path);
    }

    [Fact]
    public async Task FindDuplicatesUsesPathAndSizeFallbackWithoutHashes()
    {
        using var temp = new TemporaryDirectory();
        var path = WriteFile(temp.Path, "photo.jpg", [0x01, 0x02, 0x03, 0x04]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "a", LocalPath = path });
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "b", LocalPath = path });

        var duplicate = Assert.Single(await store.FindDuplicatesAsync());
        Assert.Equal("b", duplicate.Id);
    }

    [Fact]
    public async Task DeleteEntryRemovesMetadataAndFileWhenRequested()
    {
        using var temp = new TemporaryDirectory();
        var library = Path.Combine(temp.Path, "library");
        Directory.CreateDirectory(library);
        var image = WriteFile(library, "delete-me.jpg", [0x01, 0x02]);
        var store = new WallpaperLibraryStore(
            Path.Combine(temp.Path, "library.json"), dataDirectory: temp.Path);
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "gone", LocalPath = image });

        var result = await store.DeleteEntryWithResultAsync("gone", deleteFile: true, CancellationToken.None);

        Assert.True(result.Removed);
        Assert.True(result.FileDeleted);
        Assert.Empty(await store.GetAllAsync());
        Assert.False(File.Exists(image));
        Assert.False(await store.DeleteEntryAsync("gone", false, default));
    }

    [Fact]
    public async Task DeleteEntryKeepsFileWhenNotRequested()
    {
        using var temp = new TemporaryDirectory();
        var image = WriteFile(temp.Path, "keep-me.jpg", [0x01, 0x02]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "kept", LocalPath = image });

        Assert.True(await store.DeleteEntryAsync("kept", deleteFile: false, CancellationToken.None));

        Assert.Empty(await store.GetAllAsync());
        Assert.True(File.Exists(image));
    }

    [Fact]
    public async Task SetTagsNormalizesAndPersistsTags()
    {
        using var temp = new TemporaryDirectory();
        var image = WriteFile(temp.Path, "tagged.jpg", [0x01]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "tagged", LocalPath = image });

        Assert.True(await store.SetTagsAsync("tagged", " landscape, NATURE, landscape "));
        Assert.False(await store.SetTagsAsync("missing", "x"));

        var entry = Assert.Single(await store.GetAllAsync());
        Assert.Equal("landscape, NATURE", entry.Tags);
    }

    [Fact]
    public async Task MovedDataFolderRebasesLibraryPathsOntoTheCurrentRoot()
    {
        using var temp = new TemporaryDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        var realFile = WriteFile(images, "wallhaven-abc123.png", [0x01, 0x02]);
        var stalePath = Path.Combine(
            temp.Path, "old-root", "library", "images", "wallhaven-abc123.png");
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new { Id = "abc123", Title = "Moved", LocalPath = stalePath, Collection = "Unsorted" }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index, temp.Path).GetAllAsync());

        Assert.Equal(realFile, entry.LocalPath);
        Assert.True(File.Exists(entry.LocalPath));
    }

    [Fact]
    public async Task MissingFileWithNoMatchInTheLibraryKeepsItsStoredPath()
    {
        using var temp = new TemporaryDirectory();
        var index = Path.Combine(temp.Path, "library.json");
        var stalePath = Path.Combine(temp.Path, "old-root", "images", "gone.png");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new { Id = "gone", Title = "Gone", LocalPath = stalePath, Collection = "Unsorted" }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index).GetAllAsync());

        Assert.Equal(stalePath, entry.LocalPath);
    }

    [Fact]
    public async Task ImportedFileOutsideTheLibraryKeepsItsPath()
    {
        using var temp = new TemporaryDirectory();
        var imported = WriteFile(temp.Path, "imported.jpg", [0x01]);
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new { Id = "imp-1", Title = "Imported", LocalPath = imported, Collection = "Unsorted" }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index).GetAllAsync());

        Assert.Equal(imported, entry.LocalPath);
    }

    [Fact]
    public async Task ReSavingAnExistingEntryKeepsUserMetadata()
    {
        using var temp = new TemporaryDirectory();
        var image = WriteFile(temp.Path, "saved.jpg", [0x01]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));
        var savedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await store.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "keep-1",
            Title = "Kept",
            LocalPath = image,
            Tags = "night, city",
            Colors = "#112233",
            ContentHash = "abc",
            SavedAtUtc = savedAt
        });

        // Discover re-applies an entry after a fresh download and sends only the
        // basics, which used to wipe tags, colours, hash and the save date.
        await store.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "keep-1",
            Title = "Kept again",
            LocalPath = image
        });

        var entry = Assert.Single(await store.GetAllAsync());
        Assert.Equal("night, city", entry.Tags);
        Assert.Equal("#112233", entry.Colors);
        Assert.Equal("abc", entry.ContentHash);
        Assert.Equal(savedAt, entry.SavedAtUtc);
    }

    [Fact]
    public async Task DeleteEntryKeepsAnImportedFileOutsideTheLibrary()
    {
        using var temp = new TemporaryDirectory();
        var library = Path.Combine(temp.Path, "Ori", "library");
        var pictures = Path.Combine(temp.Path, "Pictures");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(pictures);
        var imported = WriteFile(pictures, "mine.jpg", [0x01]);
        var dataRoot = Path.Combine(temp.Path, "Ori");
        var store = new WallpaperLibraryStore(
            Path.Combine(library, "library.json"), dataDirectory: dataRoot);
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "mine", LocalPath = imported });

        Assert.True(await store.DeleteEntryAsync("mine", deleteFile: true));

        Assert.Empty(await store.GetAllAsync());
        Assert.True(File.Exists(imported));
    }

    [Fact]
    public async Task DeleteEntryRemovesAnImageInsideTheLibrary()
    {
        using var temp = new TemporaryDirectory();
        var library = Path.Combine(temp.Path, "Ori", "library");
        var images = Path.Combine(library, "images");
        Directory.CreateDirectory(images);
        var stored = WriteFile(images, "wallhaven-del.png", [0x01]);
        var dataRoot = Path.Combine(temp.Path, "Ori");
        var store = new WallpaperLibraryStore(
            Path.Combine(library, "library.json"), dataDirectory: dataRoot);
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "del", LocalPath = stored });

        Assert.True(await store.DeleteEntryAsync("del", deleteFile: true));

        Assert.Empty(await store.GetAllAsync());
        Assert.False(File.Exists(stored));
    }

    [Fact]
    public async Task WallpaperAppliedIntoTheWallpapersFolderIsRebasedThere()
    {
        using var temp = new TemporaryDirectory();
        var wallpapers = Path.Combine(temp.Path, "Wallpapers");
        var images = Path.Combine(temp.Path, "library", "images");
        Directory.CreateDirectory(wallpapers);
        Directory.CreateDirectory(images);
        // The same file name exists in both places, so the stored parent folder
        // must decide where the entry is re-pointed.
        WriteFile(images, "wallhaven-dup.jpg", [0x02]);
        var applied = WriteFile(wallpapers, "wallhaven-dup.jpg", [0x01]);
        var index = Path.Combine(temp.Path, "library", "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    Id = "dup",
                    Title = "Applied",
                    LocalPath = Path.Combine(temp.Path, "old-root", "Wallpapers", "wallhaven-dup.jpg"),
                    Collection = "Unsorted"
                }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index, temp.Path).GetAllAsync());

        Assert.Equal(applied, entry.LocalPath);
    }

    [Fact]
    public async Task CorruptIndexIsMovedAsideSoTheNextReadWorks()
    {
        using var temp = new TemporaryDirectory();
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, "{ not json");

        var store = new WallpaperLibraryStore(index);
        await Assert.ThrowsAsync<JsonException>(() => store.GetAllAsync());

        Assert.False(File.Exists(index));
        Assert.Single(Directory.GetFiles(temp.Path, "library.json.corrupt-*"));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task StaleEntryIsNotRebasedOntoADifferentFileWithTheSameName()
    {
        using var temp = new TemporaryDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        var real = WriteFile(images, "wallhaven-same.jpg", [0x01, 0x02]);
        var otherHash = Convert.ToHexString(SHA256.HashData([0x09, 0x09]));
        var stale = Path.Combine(temp.Path, "old-root", "images", "wallhaven-same.jpg");
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    Id = "same",
                    Title = "Same name",
                    LocalPath = stale,
                    Collection = "Unsorted",
                    ContentHash = otherHash
                }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index, temp.Path).GetAllAsync());

        Assert.Equal(stale, entry.LocalPath);
        Assert.True(File.Exists(real));
        Assert.Equal(otherHash, entry.ContentHash);
    }

    [Fact]
    public async Task UncPathIsNeverRebasedOntoALocalCopy()
    {
        using var temp = new TemporaryDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        WriteFile(images, "wallhaven-share.png", [0x01]);
        var index = Path.Combine(temp.Path, "library.json");
        var unc = @"\\offline-server\wallpapers\wallhaven-share.png";
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new { Id = "share", Title = "Share", LocalPath = unc, Collection = "Unsorted" }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index, temp.Path).GetAllAsync());

        Assert.Equal(unc, entry.LocalPath);
    }

    [Fact]
    public async Task CustomIndexPathCannotDeleteFilesOutsideTheDataRoot()
    {
        using var temp = new TemporaryDirectory();
        var dataRoot = Path.Combine(temp.Path, "Ori");
        var userFolder = Path.Combine(temp.Path, "Pictures");
        Directory.CreateDirectory(Path.Combine(dataRoot, "library"));
        Directory.CreateDirectory(userFolder);
        var image = WriteFile(userFolder, "keep.jpg", [0x01]);
        // The index sits beside a user's files: ownership must come from dataRoot.
        var store = new WallpaperLibraryStore(
            Path.Combine(userFolder, "library.json"), dataDirectory: dataRoot);
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "keep", LocalPath = image });

        var result = await store.DeleteEntryWithResultAsync("keep", deleteFile: true);

        Assert.True(result.Removed);
        Assert.False(result.FileDeleted);
        Assert.True(File.Exists(image));
    }

    [Fact]
    public async Task ImportFolderSkipsFormatsWindowsDesktopWallpaperCannotApply()
    {
        using var temp = new TemporaryDirectory();
        var folder = Path.Combine(temp.Path, "pictures");
        Directory.CreateDirectory(folder);
        WriteFile(folder, "a.jpg", [0x01]);
        WriteFile(folder, "b.png", [0x02]);
        WriteFile(folder, "c.webp", [0x03]);
        WriteFile(folder, "d.gif", [0x04]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        Assert.Equal(2, await store.ImportFolderAsync(folder));

        var entries = await store.GetAllAsync();
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.LocalPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.LocalPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComputeSha256MatchesKnownHashAndReturnsNullForMissingFile()
    {
        using var temp = new TemporaryDirectory();
        byte[] content = [0x01, 0x02, 0x03, 0x04];
        var path = WriteFile(temp.Path, "hash.jpg", content);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), WallpaperLibraryStore.ComputeSha256(path));
        Assert.Null(WallpaperLibraryStore.ComputeSha256(Path.Combine(temp.Path, "missing.jpg")));
    }

    [Fact]
    public async Task PersistRepairedPathsWritesOnlyWhenAPathChanged()
    {
        using var temp = new TemporaryDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        var moved = WriteFile(images, "moved.png", [0x01, 0x02]);
        var stale = Path.Combine(temp.Path, "old-root", "images", "moved.png");
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new { Id = "moved", Title = "Moved", LocalPath = stale, Collection = "Unsorted" }
            }
        }));
        var store = new WallpaperLibraryStore(index, temp.Path);

        Assert.True(await store.PersistRepairedPathsAsync());

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(index)))
        {
            var storedPath = document.RootElement.GetProperty("Entries")[0].GetProperty("LocalPath").GetString();
            Assert.Equal(moved, storedPath);
        }
        Assert.False(await store.PersistRepairedPathsAsync());
    }

    [Fact]
    public async Task NullEntryInIndexIsSkippedAndTheIndexIsBackedUp()
    {
        using var temp = new TemporaryDirectory();
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new object?[]
            {
                null,
                new { Id = "ok", Title = "Kept", LocalPath = "", Collection = "Unsorted" }
            }
        }));

        var entries = await new WallpaperLibraryStore(index).GetAllAsync();

        Assert.Equal("ok", Assert.Single(entries).Id);
        Assert.False(File.Exists(index));
        Assert.Single(Directory.GetFiles(temp.Path, "library.json.corrupt-*"));
    }

    [Fact]
    public async Task OversizedIndexIsBackedUpBeforeThrowing()
    {
        using var temp = new TemporaryDirectory();
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllBytesAsync(index, new byte[8 * 1024 * 1024 + 1]);
        var store = new WallpaperLibraryStore(index);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());

        Assert.False(File.Exists(index));
        var backup = Assert.Single(Directory.GetFiles(temp.Path, "library.json.corrupt-*"));
        Assert.Equal(8 * 1024 * 1024 + 1, new FileInfo(backup).Length);
    }

    [Fact]
    public async Task OwnedFileDeleteFailureKeepsTheEntryForRetry()
    {
        // Windows refuses to delete a file opened with FileShare.None; other
        // platforms permit it, so the locked-file scenario is Windows-specific.
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var dataRoot = Path.Combine(temp.Path, "Ori");
        var images = Path.Combine(dataRoot, "library", "images");
        Directory.CreateDirectory(images);
        var image = WriteFile(images, "locked.jpg", [0x01, 0x02]);
        var store = new WallpaperLibraryStore(
            Path.Combine(dataRoot, "library", "library.json"), dataRoot);
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "locked", LocalPath = image });

        using (var lockStream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await store.DeleteEntryWithResultAsync("locked", deleteFile: true);

            Assert.False(failed.Removed);
            Assert.False(failed.FileDeleted);
            Assert.Equal("locked", Assert.Single(await store.GetAllAsync()).Id);
            Assert.True(File.Exists(image));
        }

        var retried = await store.DeleteEntryWithResultAsync("locked", deleteFile: true);

        Assert.True(retried.Removed);
        Assert.True(retried.FileDeleted);
        Assert.Empty(await store.GetAllAsync());
        Assert.False(File.Exists(image));
    }

    [Fact]
    public async Task ReSavingWithAChangedPathRecomputesTheContentHash()
    {
        using var temp = new TemporaryDirectory();
        var first = WriteFile(temp.Path, "first.jpg", [0x01]);
        var second = WriteFile(temp.Path, "second.jpg", [0x02, 0x03]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));
        await store.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "swap", LocalPath = first, ContentHash = "OLD-HASH"
        });

        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "swap", LocalPath = second });

        var entry = Assert.Single(await store.GetAllAsync());
        Assert.Equal(second, entry.LocalPath);
        Assert.Equal(WallpaperLibraryStore.ComputeSha256(second), entry.ContentHash);
        Assert.NotEqual("OLD-HASH", entry.ContentHash);
    }

    [Fact]
    public async Task HashlessImportFromAnotherFolderIsNotRebasedOntoAlikeNamedFile()
    {
        using var temp = new TemporaryDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        WriteFile(images, "photo.jpg", [0x01, 0x02]);
        var stale = Path.Combine(temp.Path, "Pictures", "photo.jpg");
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new { Id = "imp", Title = "Imported", LocalPath = stale, Collection = "Unsorted" }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index, temp.Path).GetAllAsync());

        Assert.Equal(stale, entry.LocalPath);
    }

    [Fact]
    public async Task MatchingHashStillAllowsRebaseFromAnArbitraryParent()
    {
        using var temp = new TemporaryDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        var actual = WriteFile(images, "photo.jpg", [0x01, 0x02]);
        var hash = Convert.ToHexString(SHA256.HashData([0x01, 0x02]));
        var stale = Path.Combine(temp.Path, "Pictures", "photo.jpg");
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    Id = "imp", Title = "Imported", LocalPath = stale, Collection = "Unsorted", ContentHash = hash
                }
            }
        }));

        var entry = Assert.Single(await new WallpaperLibraryStore(index, temp.Path).GetAllAsync());

        Assert.Equal(actual, entry.LocalPath);
    }

    [Fact]
    public async Task ImportFolderSkipsASubdirectoryThatDisappearsMidScan()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "pictures");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteFile(first, "in-first.jpg", [0x01]);
        WriteFile(second, "in-second.jpg", [0x02]);

        var messages = new List<string>();
        var removed = false;
        var progress = new InlineProgress(message =>
        {
            messages.Add(message);
            if (!removed && message.StartsWith("Hashing ", StringComparison.Ordinal))
            {
                // Both folders are already on the scan stack. Delete the one that has
                // not been visited yet so its enumeration throws DirectoryNotFound.
                var unvisited = message.Contains("in-first.jpg", StringComparison.Ordinal) ? second : first;
                Directory.Delete(unvisited, recursive: true);
                removed = true;
            }
        });
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        var imported = await store.ImportFolderAsync(root, progress, CancellationToken.None);

        Assert.True(removed);
        Assert.Equal(1, imported);
        Assert.Contains(messages, message => message.StartsWith("Skipped folder", StringComparison.Ordinal));
        Assert.Single(await store.GetAllAsync());
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static string WriteFile(string directory, string name, byte[] content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OriFeatureTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
