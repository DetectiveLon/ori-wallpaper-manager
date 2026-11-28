using System.Text.Json;
using Ori.Core.Services;

namespace Ori.Core.Tests;

public sealed class ScheduleStoreContractTests
{
    [Fact]
    public void FingerprintIsStableForSameDeviceAndGeometry()
    {
        var first = ScheduleStore.BuildFingerprint("  DELL U2723QE ", 1920, 0, 2560, 1440);
        var same = ScheduleStore.BuildFingerprint("DELL U2723QE", 1920, 0, 2560, 1440);
        var changedGeometry = ScheduleStore.BuildFingerprint("DELL U2723QE", 1920, 0, 1920, 1080);
        var changedDevice = ScheduleStore.BuildFingerprint("OTHER", 1920, 0, 2560, 1440);

        Assert.Equal(first, same);
        Assert.NotEqual(first, changedGeometry);
        Assert.NotEqual(first, changedDevice);
    }

    [Fact]
    public async Task DuplicateMonitorIdsKeepTheLastEntryInTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ori-schedules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Version = 1,
                Entries = new[]
                {
                    new
                    {
                        MonitorId = "display1",
                        IntervalName = "1 minute",
                        ShouldRun = false,
                        UserPaused = false,
                        NextRunUtc = (DateTimeOffset?)null,
                        LastAppliedId = "first-wall"
                    },
                    new
                    {
                        MonitorId = "DISPLAY1",
                        IntervalName = "5 minutes",
                        ShouldRun = true,
                        UserPaused = true,
                        NextRunUtc = (DateTimeOffset?)null,
                        LastAppliedId = "last-wall"
                    }
                }
            }));

            var loaded = await new ScheduleStore(path).LoadAsync();

            var state = Assert.Single(loaded.Values);
            Assert.Equal("last-wall", state.LastAppliedId);
            Assert.Equal("5 minutes", state.IntervalName);
            Assert.True(state.UserPaused);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptScheduleFileIsMovedAsideBeforeStartingEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ori-schedules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "schedules.json");
        await File.WriteAllTextAsync(path, "{ not json");
        try
        {
            Assert.Empty(await new ScheduleStore(path).LoadAsync());

            Assert.False(File.Exists(path));
            var backup = Assert.Single(Directory.GetFiles(directory, "schedules.json.corrupt-*"));
            Assert.Contains("{ not json", await File.ReadAllTextAsync(backup));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NullEntryInScheduleFileIsSkippedAndBackedUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ori-schedules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "schedules.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new object?[]
            {
                null,
                new
                {
                    MonitorId = "DISPLAY1",
                    IntervalName = "5 minutes",
                    ShouldRun = true,
                    UserPaused = false,
                    NextRunUtc = (DateTimeOffset?)null,
                    LastAppliedId = "wall-1"
                }
            }
        }));
        try
        {
            var loaded = await new ScheduleStore(path).LoadAsync();

            Assert.Equal("wall-1", Assert.Single(loaded.Values).LastAppliedId);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(directory, "schedules.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedScheduleFileIsQuarantinedBeforeItCanBeOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ori-schedules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "schedules.json");
        var oversized = new byte[1024 * 1024 + 1];
        oversized[0] = (byte)'{';
        await File.WriteAllBytesAsync(path, oversized);
        try
        {
            var store = new ScheduleStore(path);

            Assert.Empty(await store.LoadAsync());
            Assert.False(File.Exists(path));
            var backup = Assert.Single(Directory.GetFiles(directory, "schedules.json.corrupt-*"));
            Assert.Equal(oversized.Length, new FileInfo(backup).Length);

            await store.SaveAsync([new DisplayScheduleState("DISPLAY1", "1 minute", true, false, null, null)]);
            Assert.Single(await store.LoadAsync());
            Assert.Single(Directory.GetFiles(directory, "schedules.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreviousAppliedPathRoundTripsThroughSchedulesJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ori-schedules-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ScheduleStore(path);
            const string previous = @"C:\Wallpapers\previous.jpg";
            await store.SaveAsync([
                new DisplayScheduleState(
                    "DISPLAY1", "5 minutes", true, false, null, "wall-1",
                    PreviousAppliedPath: previous)
            ]);

            var loaded = Assert.Single((await store.LoadAsync()).Values);
            Assert.Equal(previous, loaded.PreviousAppliedPath);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(previous, document.RootElement.GetProperty("Entries")[0]
                .GetProperty("PreviousAppliedPath").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
