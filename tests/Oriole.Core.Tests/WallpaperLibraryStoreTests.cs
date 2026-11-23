using System.Text.Json;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

public sealed class WallpaperLibraryStoreTests
{
    [Fact]
    public async Task UpsertPersistsEntriesAcrossStoreInstances()
    {
        using var temp = new TemporaryDirectory();
        var firstImage = CreateImageFile(temp.Path, "first.jpg");
        var secondImage = CreateImageFile(temp.Path, "second.jpg");
        var index = Path.Combine(temp.Path, "library.json");
        var firstStore = new WallpaperLibraryStore(index);
        var secondStore = new WallpaperLibraryStore(index);

        await firstStore.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "abc123",
            Title = "First",
            LocalPath = firstImage
        });
        await secondStore.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "def456",
            Title = "Second",
            LocalPath = secondImage
        });

        var entries = await new WallpaperLibraryStore(index).GetAllAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Id == "abc123");
        Assert.Contains(entries, entry => entry.Id == "def456");
    }

    [Fact]
    public async Task ConcurrentWritersAcrossStoreInstancesDoNotLoseEntries()
    {
        using var temp = new TemporaryDirectory();
        var index = Path.Combine(temp.Path, "library.json");
        const int entryCount = 20;
        var writes = Enumerable.Range(0, entryCount).Select(async value =>
        {
            var imagePath = CreateImageFile(temp.Path, $"wallpaper-{value}.jpg");
            var store = new WallpaperLibraryStore(index);
            await store.UpsertAsync(new WallpaperLibraryEntry
            {
                Id = $"id{value:D2}",
                Title = $"Wallpaper {value}",
                LocalPath = imagePath
            });
        });

        await Task.WhenAll(writes);

        var entries = await new WallpaperLibraryStore(index).GetAllAsync();
        Assert.Equal(entryCount, entries.Count);
    }

    [Fact]
    public async Task ReSavingEntryPreservesFavoriteAndCollection()
    {
        using var temp = new TemporaryDirectory();
        var imagePath = CreateImageFile(temp.Path, "portrait.jpg");
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));

        await store.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "abc123",
            Title = "Original title",
            LocalPath = imagePath,
            IsFavorite = true,
            Collection = "Portraits"
        });

        await store.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "abc123",
            Title = "Updated title",
            LocalPath = imagePath
        });

        var entry = Assert.Single(await store.GetAllAsync());
        Assert.Equal("Updated title", entry.Title);
        Assert.True(entry.IsFavorite);
        Assert.Equal("Portraits", entry.Collection);
    }

    [Fact]
    public async Task RemovingEntryKeepsWallpaperFileOnDisk()
    {
        using var temp = new TemporaryDirectory();
        var imagePath = CreateImageFile(temp.Path, "wallpaper.jpg");
        var store = new WallpaperLibraryStore(Path.Combine(temp.Path, "library.json"));
        await store.UpsertAsync(new WallpaperLibraryEntry
        {
            Id = "abc123",
            Title = "Wallpaper",
            LocalPath = imagePath
        });

        Assert.True(await store.RemoveAsync("abc123"));

        Assert.Empty(await store.GetAllAsync());
        Assert.True(File.Exists(imagePath));
    }

    [Fact]
    public async Task CorruptIndexIsReportedInsteadOfTreatedAsEmpty()
    {
        using var temp = new TemporaryDirectory();
        var index = Path.Combine(temp.Path, "library.json");
        await File.WriteAllTextAsync(index, "{ this is not valid json");

        await Assert.ThrowsAsync<JsonException>(() =>
            new WallpaperLibraryStore(index).GetAllAsync());
    }

    private static string CreateImageFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OrioleTests",
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
