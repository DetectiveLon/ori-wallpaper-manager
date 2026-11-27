using Ori.Desktop.Services;

namespace Ori.Desktop.Tests;

/// <summary>
/// Start-minimized is a new per-user preference. A fresh install must keep the
/// tray-only startup, and an explicit choice must survive a save/load cycle in
/// the app's data directory (redirected to a temp folder for these tests).
/// </summary>
public sealed class UiPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ori-ui-preferences-tests", Guid.NewGuid().ToString("N"));

    public UiPreferencesStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void New_store_defaults_to_start_minimized()
    {
        Assert.True(new UiPreferencesStore(_directory).StartMinimized);
        Assert.Equal(Path.Combine(_directory, "ui-preferences.json"),
            new UiPreferencesStore(_directory).FilePath);
    }

    [Fact]
    public void Missing_preferences_file_uses_the_default()
    {
        Assert.True(UiPreferencesStore.Load(_directory).StartMinimized);
        Assert.False(File.Exists(Path.Combine(_directory, "ui-preferences.json")));
    }

    [Fact]
    public void StartMinimized_round_trips_to_the_data_directory()
    {
        var store = UiPreferencesStore.Load(_directory);
        Assert.True(store.StartMinimized);

        store.StartMinimized = false;
        store.Save();

        var reloaded = UiPreferencesStore.Load(_directory);
        Assert.False(reloaded.StartMinimized);

        reloaded.StartMinimized = true;
        reloaded.Save();
        Assert.True(UiPreferencesStore.Load(_directory).StartMinimized);
    }

    [Fact]
    public void Corrupt_preferences_fall_back_to_the_default()
    {
        File.WriteAllText(Path.Combine(_directory, "ui-preferences.json"), "{ not json");

        Assert.True(UiPreferencesStore.Load(_directory).StartMinimized);
    }
}
