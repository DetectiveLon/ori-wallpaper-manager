using System.Text.Json;
using Ori.Core.Services;
using Ori.Desktop.Services;
using Ori.Desktop.ViewModels;
using System.Windows.Media;

namespace Ori.Desktop.Tests;

/// <summary>
/// Pure unit coverage for the Displays rebuild: fingerprint identity, atomic
/// per-display preference persistence, corrupt-file quarantine and the fit
/// preview converter. No COM calls and no WPF visuals are involved.
/// </summary>
public sealed class DisplayRebuildTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ori-display-rebuild-tests", Guid.NewGuid().ToString("N"));

    public DisplayRebuildTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string PreferencesPath => Path.Combine(_directory, "settings", "display-preferences.json");

    [Fact]
    public void Store_uses_the_documented_settings_path_with_safe_defaults()
    {
        var store = new DisplayPreferencesStore(_directory);

        Assert.Equal(PreferencesPath, store.FilePath);
        Assert.False(store.SpanActive);
        Assert.Equal(DisplayFit.Fill, store.GetFit("\\\\.\\DISPLAY1|0,0,1920x1080"));
        Assert.False(File.Exists(PreferencesPath));
    }

    [Fact]
    public void Fit_source_and_selection_round_trip_per_panel()
    {
        const string first = "\\\\.\\DISPLAY1|0,0,1920x1080";
        const string second = "\\\\.\\DISPLAY2|1920,0,1920x1080";

        var store = DisplayPreferencesStore.Load(_directory);
        store.SetFit(first, DisplayFit.Tile);
        store.SetImage(first, "C:\\wall\\one.png", "Library", "entry-1");
        store.SetFit(second, DisplayFit.Fit);
        store.SetSelection(second);
        store.SetSpan(false, DisplayFit.Stretch, first, null);
        store.Save();

        var reloaded = DisplayPreferencesStore.Load(_directory);
        Assert.Equal(DisplayFit.Tile, reloaded.GetFit(first));
        Assert.Equal("C:\\wall\\one.png", reloaded.GetImagePath(first));
        var source = reloaded.GetSource(first);
        Assert.Equal("Library", source.Kind);
        Assert.Equal("entry-1", source.Id);
        Assert.Equal(DisplayFit.Fit, reloaded.GetFit(second));
        Assert.Equal(second, reloaded.SelectedFingerprint);
        Assert.Equal(DisplayFit.Stretch, reloaded.SpanPreviousFit);
        Assert.Equal(first, reloaded.SpanPreviousFingerprint);
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temporary_files()
    {
        var store = new DisplayPreferencesStore(_directory);
        store.SetFit("panel", DisplayFit.Center);
        store.Save();

        Assert.True(File.Exists(PreferencesPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(PreferencesPath)!, "*.tmp"));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_instead_of_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, "{ this is not json");

        var store = DisplayPreferencesStore.Load(_directory);

        Assert.Equal(DisplayFit.Fill, store.GetFit("panel"));
        Assert.False(File.Exists(PreferencesPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(PreferencesPath)!, "display-preferences.json.corrupt-*"));
    }

    [Fact]
    public void Unsupported_schema_is_quarantined()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, "{\"Version\":99,\"Displays\":[]}");

        var store = DisplayPreferencesStore.Load(_directory);

        Assert.Empty(store.Entries);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(PreferencesPath)!, "display-preferences.json.corrupt-*"));
    }

    [Fact]
    public void Unknown_fields_are_tolerated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath,
            "{\"Version\":1,\"Displays\":[{\"Fingerprint\":\"panel\",\"Fit\":\"Stretch\",\"ImagePath\":null,\"SourceKind\":null,\"SourceId\":null,\"SavedAtUtc\":null,\"FutureField\":7}],\"SelectedFingerprint\":\"panel\",\"FutureRoot\":true}");

        var store = DisplayPreferencesStore.Load(_directory);

        Assert.Equal(DisplayFit.Stretch, store.GetFit("panel"));
        Assert.Equal("panel", store.SelectedFingerprint);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(PreferencesPath)!, "display-preferences.json.corrupt-*"));
    }

    [Fact]
    public void Fingerprint_matches_the_automation_schedule_identity()
    {
        var monitor = new DisplayMonitor(
            "\\\\?\\DISPLAY\\stale-path",
            "\\\\.\\DISPLAY2",
            1920,
            0,
            1920,
            1080,
            false,
            null);

        Assert.Equal(
            ScheduleStore.BuildFingerprint(monitor.DeviceName, monitor.Left, monitor.Top, monitor.Width, monitor.Height),
            DisplayDetection.Fingerprint(monitor));
        Assert.Equal(monitor.Fingerprint, DisplayDetection.Fingerprint(monitor));
    }

    [Fact]
    public void Display_number_comes_from_windows_device_name_not_com_index()
    {
        var monitor = new DisplayMonitor(
            "\\\\?\\DISPLAY\\instance-9",
            "\\\\.\\DISPLAY2",
            0,
            0,
            1920,
            1080,
            true,
            null);

        var card = new DisplayCardViewModel(monitor, index: 0);

        Assert.Equal("Display 2", monitor.DisplayNumberText);
        Assert.Equal("Display 2", card.DisplayName);
        Assert.Equal("No image available", card.ImageName);
    }

    [Fact]
    public void Monitor_selection_load_clears_stale_ids()
    {
        var path = Path.Combine(_directory, "settings", "selected-monitor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { monitorId = "unplugged" }));

        var service = new MonitorSelectionService(path);
        service.Load(new[] { "live-path" });
        Assert.Null(service.SelectedMonitorId);

        File.WriteAllText(path, JsonSerializer.Serialize(new { monitorId = "live-path" }));
        service.Load(new[] { "live-path" });
        Assert.Equal("live-path", service.SelectedMonitorId);
        Assert.Equal("live-path", service.Resolve("primary"));
    }

    [Fact]
    public void Fit_converter_maps_the_enum_without_fit_booleans()
    {
        var converter = new DisplayFitPreviewConverter();
        object[] values = [DisplayFit.Fill, false];

        Assert.Equal(Stretch.UniformToFill,
            (Stretch)converter.Convert(values, typeof(Stretch), "Stretch", System.Globalization.CultureInfo.InvariantCulture)!);
        Assert.Equal(TileMode.None,
            (TileMode)converter.Convert(values, typeof(TileMode), "TileMode", System.Globalization.CultureInfo.InvariantCulture)!);

        object[] tileValues = [DisplayFit.Tile, false];
        Assert.Equal(TileMode.Tile,
            (TileMode)converter.Convert(tileValues, typeof(TileMode), "TileMode", System.Globalization.CultureInfo.InvariantCulture)!);
        Assert.Equal(BrushMappingMode.Absolute,
            (BrushMappingMode)converter.Convert(tileValues, typeof(BrushMappingMode), "ViewportUnits", System.Globalization.CultureInfo.InvariantCulture)!);

        object[] spanValues = [DisplayFit.Fill, true];
        Assert.Equal(Stretch.Fill,
            (Stretch)converter.Convert(spanValues, typeof(Stretch), "Stretch", System.Globalization.CultureInfo.InvariantCulture)!);
    }
}
