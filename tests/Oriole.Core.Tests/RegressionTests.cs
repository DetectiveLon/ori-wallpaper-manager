using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

public sealed class RegressionTests
{
    [Fact]
    public async Task ThrowingDeadlineCallbackDoesNotFaultTheLoop()
    {
        var loop = new RotationLoop();
        using var cancellation = new CancellationTokenSource();
        var run = loop.RunAsync(
            TimeSpan.FromMinutes(1),
            shouldPause: () => false,
            rotateAsync: _ => Task.CompletedTask,
            nextRunChanged: _ => throw new InvalidOperationException("UI is gone"),
            cancellationToken: cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(run.IsFaulted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void AutoRedirectingClientIsRejected()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        Assert.Throws<ArgumentException>(() => new WallhavenClient(http));
    }

    [Fact]
    public void OutOfRangeOrderIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WallhavenQueryBuilder.Build(new WallhavenFilters { Order = (WallhavenOrder)999 }));
    }

    [Fact]
    public async Task EntryCanBeMovedBackToUnsorted()
    {
        var (store, _, imagePath, cleanup) = CreateStore();
        try
        {
            var entry = SampleEntry(imagePath) with { Collection = "Favorites" };
            await store.UpsertAsync(entry);

            Assert.True(await store.SetCollectionAsync(entry.Id, "Unsorted"));
            var entries = await store.GetAllAsync();
            Assert.Equal("Unsorted", Assert.Single(entries).Collection);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task RelativeIndexPathCanBeWritten()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"oriole-image-{Guid.NewGuid():N}.jpg");
        var indexName = $"oriole-index-{Guid.NewGuid():N}.json";
        var fullIndexPath = Path.GetFullPath(indexName);
        File.WriteAllText(imagePath, "not-a-real-image");
        try
        {
            var store = new WallpaperLibraryStore(indexName);
            await store.UpsertAsync(SampleEntry(imagePath));
            Assert.True(File.Exists(fullIndexPath));
        }
        finally
        {
            File.Delete(imagePath);
            File.Delete(fullIndexPath);
        }
    }

    private static (WallpaperLibraryStore Store, string IndexPath, string ImagePath, Action Cleanup) CreateStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"oriole-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var indexPath = Path.Combine(directory, "library.json");
        var imagePath = Path.Combine(directory, "wallpaper.jpg");
        File.WriteAllText(imagePath, "not-a-real-image");
        return (new WallpaperLibraryStore(indexPath), indexPath, imagePath,
            () => Directory.Delete(directory, recursive: true));
    }

    private static WallpaperLibraryEntry SampleEntry(string imagePath) => new()
    {
        Id = "sample-1",
        Title = "Sample",
        LocalPath = imagePath,
        ImageUrl = "https://wallhaven.cc/w/sample",
        Width = 1920,
        Height = 1080
    };
}
