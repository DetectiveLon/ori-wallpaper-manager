using System.Text.Json;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

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
            "OrioleFeatureTests",
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
