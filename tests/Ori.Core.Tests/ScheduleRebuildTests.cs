using System.Linq;
using System.Text.Json;
using Ori.Core.Services;

namespace Ori.Core.Tests;

/// <summary>Schedule schema v2 migration, custom intervals and per-display source binding.</summary>
public sealed class ScheduleRebuildTests
{
    // ----- custom interval parsing -----

    [Theory]
    [InlineData("1 minute", 1)]
    [InlineData("5 minutes", 5)]
    [InlineData("30 minutes", 30)]
    [InlineData("1 hour", 60)]
    [InlineData("2 hours", 120)]
    [InlineData("6 hours", 360)]
    [InlineData("12 hours", 720)]
    [InlineData("1 day", 1440)]
    [InlineData("2 days", 2880)]
    [InlineData("3 days", 4320)]
    [InlineData("7 days", 10080)]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("90", 90)]
    [InlineData("10080", 10080)]
    [InlineData("7 minutes", 7)]
    [InlineData("7 mins", 7)]
    [InlineData("45 minutes", 45)]
    [InlineData("3 hours", 180)]
    [InlineData("4 days", 5760)]
    [InlineData("  30 minutes  ", 30)]
    public void CustomIntervalParsesArbitraryWholeMinutes(string name, int expectedMinutes)
    {
        Assert.True(ScheduleStore.TryParseIntervalName(name, out var minutes), name);
        Assert.Equal(expectedMinutes, minutes);
    }

    [Theory]
    [InlineData("1 minute", 1)]
    [InlineData("1 hour", 60)]
    [InlineData("1 day", 1440)]
    [InlineData("7 days", 10080)]
    public void LegacyNamesStillParse(string name, int expectedMinutes)
    {
        Assert.True(ScheduleStore.TryParseIntervalName(name, out var minutes));
        Assert.Equal(expectedMinutes, minutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0 minutes")]
    [InlineData("-5")]
    [InlineData("10081")]
    [InlineData("10081 minutes")]
    [InlineData("10001 days")]
    [InlineData("1.5")]
    [InlineData("1.5 minutes")]
    [InlineData("abc")]
    [InlineData("minutes")]
    [InlineData("half hour")]
    [InlineData("10080.5")]
    public void OutOfRangeOrNonWholeMinuteIntervalsAreRejected(string? name)
    {
        Assert.False(ScheduleStore.TryParseIntervalName(name, out _));
    }

    [Fact]
    public void BoundsAreOneToTenThousandEightyMinutes()
    {
        Assert.Equal(1, ScheduleStore.MinimumIntervalMinutes);
        Assert.Equal(10080, ScheduleStore.MaximumIntervalMinutes);

        Assert.True(ScheduleStore.TryParseIntervalName("1", out var low));
        Assert.True(ScheduleStore.TryParseIntervalName("10080", out var high));
        Assert.Equal(1, low);
        Assert.Equal(10080, high);

        Assert.False(ScheduleStore.TryParseIntervalName("0", out _));
        Assert.False(ScheduleStore.TryParseIntervalName("10081", out _));
    }

    [Fact]
    public void FormatIntervalNameRoundTripsCustomMinutes()
    {
        foreach (var minutes in new[] { 1, 7, 13, 59, 60, 61, 90, 999, 10080 })
        {
            var formatted = ScheduleStore.FormatIntervalName(minutes);
            Assert.True(ScheduleStore.TryParseIntervalName(formatted, out var parsed), formatted);
            Assert.Equal(minutes, parsed);
        }
    }

    // ----- schema v2 migration -----

    [Fact]
    public async Task V1FileIsReadAndMigratedToSchemaV2()
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            // A genuine v1 file: Version=1, no SourceKind/SourceId fields.
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Version = 1,
                Entries = new[]
                {
                    new
                    {
                        MonitorId = "DISPLAY1",
                        IntervalName = "5 minutes",
                        ShouldRun = true,
                        UserPaused = false,
                        NextRunUtc = (DateTimeOffset?)null,
                        LastAppliedId = "wall-1",
                        PauseWhenFullscreen = true,
                        MonitorFingerprint = "DELL|0,0,1920x1080",
                        PreviousAppliedPath = @"C:\Wallpapers\a.jpg"
                    }
                }
            }));

            var store = new ScheduleStore(path);
            var loaded = await store.LoadAsync();
            var state = Assert.Single(loaded.Values);

            // v1 content survives...
            Assert.Equal("5 minutes", state.IntervalName);
            Assert.True(state.ShouldRun);
            Assert.Equal("wall-1", state.LastAppliedId);
            Assert.Equal(@"C:\Wallpapers\a.jpg", state.PreviousAppliedPath);
            // ...and per-display source gets safe v2 defaults.
            Assert.Equal(RotationSourceKind.Library, state.SourceKind);
            Assert.Null(state.SourceId);

            // Re-saving writes schema v2.
            await store.SaveAsync(loaded.Values.ToArray());
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(ScheduleStore.SchemaVersion, document.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal(2, ScheduleStore.SchemaVersion);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task V1FileWithUnknownFieldsIsTolerated()
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{ \"Version\": 1, \"SomethingNew\": 123, \"Entries\": [" +
                "{ \"MonitorId\": \"D1\", \"IntervalName\": \"1 hour\", \"ShouldRun\": true, \"UserPaused\": false, " +
                "\"NextRunUtc\": null, \"LastAppliedId\": null, \"FutureField\": \"x\" } ] }");

            var loaded = await new ScheduleStore(path).LoadAsync();
            var state = Assert.Single(loaded.Values);
            Assert.Equal("1 hour", state.IntervalName);
            Assert.Equal(RotationSourceKind.Library, state.SourceKind);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnsupportedSchemaVersionIsQuarantined()
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 99, Entries = Array.Empty<object>() }));
            var loaded = await new ScheduleStore(path).LoadAsync();
            Assert.Empty(loaded);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(directory, "schedules.json.corrupt-*"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnknownSourceKindStringFallsBackWithoutQuarantiningSchedules()
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{ \"Version\": 2, \"Entries\": [" +
                "{ \"MonitorId\": \"D1\", \"IntervalName\": \"1 hour\", \"ShouldRun\": true, \"UserPaused\": false, " +
                "\"NextRunUtc\": null, \"LastAppliedId\": null, \"SourceKind\": \"FromTheFuture\", \"SourceId\": \"x\" } ] }");

            var loaded = await new ScheduleStore(path).LoadAsync();
            var state = Assert.Single(loaded.Values);

            Assert.Equal(RotationSourceKind.Library, state.SourceKind);
            Assert.Equal("x", state.SourceId);
            Assert.False(Directory.GetFiles(directory, "schedules.json.corrupt-*").Any());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ----- per-display source binding round trip -----

    [Fact]
    public async Task SourceKindAndSourceIdRoundTrip()
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            var store = new ScheduleStore(path);
            await store.SaveAsync(
            [
                new DisplayScheduleState(
                    "DISPLAY1", "17 minutes", true, false, null, "w",
                    PauseWhenFullscreen: true,
                    SourceKind: RotationSourceKind.Feed,
                    SourceId: "my-feed"),
                new DisplayScheduleState(
                    "DISPLAY2", "1 hour", false, false, null, null,
                    SourceKind: RotationSourceKind.Collection,
                    SourceId: "Nature")
            ]);

            var loaded = await store.LoadAsync();

            var feed = loaded["DISPLAY1"];
            Assert.Equal(RotationSourceKind.Feed, feed.SourceKind);
            Assert.Equal("my-feed", feed.SourceId);
            Assert.Equal("17 minutes", feed.IntervalName);
            // custom whole-minute interval persisted in IntervalName
            Assert.True(ScheduleStore.TryParseIntervalName(feed.IntervalName, out var m));
            Assert.Equal(17, m);

            var collection = loaded["DISPLAY2"];
            Assert.Equal(RotationSourceKind.Collection, collection.SourceKind);
            Assert.Equal("Nature", collection.SourceId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DisplayScheduleStateSourceDefaults()
    {
        var state = new DisplayScheduleState("D1", "1 minute", true, false, null, null);
        Assert.Equal(RotationSourceKind.Library, state.SourceKind);
        Assert.Null(state.SourceId);
    }

    [Fact]
    public async Task SourceKindPersistsAsStringInJson()
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            var store = new ScheduleStore(path);
            await store.SaveAsync([new DisplayScheduleState(
                "D1", "1 minute", true, false, null, null, SourceKind: RotationSourceKind.Feed, SourceId: "f")]);
            var raw = await File.ReadAllTextAsync(path);
            Assert.Contains("Feed", raw);
            // round trip reads it back
            Assert.Equal(RotationSourceKind.Feed, (await store.LoadAsync())["D1"].SourceKind);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ----- custom interval persisted in IntervalName -----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(90)]
    [InlineData(10080)]
    public async Task CustomWholeMinuteIntervalRoundTripsInIntervalName(int minutes)
    {
        var directory = NewTempDir();
        var path = Path.Combine(directory, "schedules.json");
        try
        {
            var store = new ScheduleStore(path);
            var intervalName = ScheduleStore.FormatIntervalName(minutes);
            await store.SaveAsync([new DisplayScheduleState("D1", intervalName, true, false, null, null)]);

            var loaded = Assert.Single((await store.LoadAsync()).Values);
            Assert.True(ScheduleStore.TryParseIntervalName(loaded.IntervalName, out var parsed));
            Assert.Equal(minutes, parsed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewTempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ori-schedules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
