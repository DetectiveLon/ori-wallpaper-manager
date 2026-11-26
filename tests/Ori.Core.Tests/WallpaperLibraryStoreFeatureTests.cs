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
        var image = WriteFile(temp.Path, "delete-me.jpg", [0x01, 0x02]);
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));
        await store.UpsertAsync(new WallpaperLibraryEntry { Id = "gone", LocalPath = image });

        Assert.True(await store.DeleteEntryAsync("gone", deleteFile: true, CancellationToken.None));

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

        var entry = Assert.Single(await new WallpaperLibraryStore(index).GetAllAsync());

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
        var store = new WallpaperLibraryStore(Path.Combine(library, "library.json"));
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
        var store = new WallpaperLibraryStore(Path.Combine(library, "library.json"));
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

        var entry = Assert.Single(await new WallpaperLibraryStore(index).GetAllAsync());

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
