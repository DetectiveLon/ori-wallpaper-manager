using System.Diagnostics;
using System.Windows.Input;
using Oriole.Desktop.Mvvm;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.ViewModels;

/// <summary>View model for the Settings page: API key, cache, local data and about info.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oriole");
    private static readonly string CacheDirectory = Path.Combine(DataRoot, "cache", "images");
    private static readonly string LibraryDirectory = Path.Combine(DataRoot, "library");
    private static readonly string LogDirectory = Path.Combine(DataRoot, "logs");

    private string _apiKey = string.Empty;
    private string _keyStatus = string.Empty;
    private string _cacheStatus = string.Empty;
    private string _status = string.Empty;
    private int _cacheFileCount;
    private long _cacheSizeBytes;

    public SettingsViewModel()
    {
        SaveApiKeyCommand = new RelayCommand(_ => SaveApiKey());
        RemoveApiKeyCommand = new RelayCommand(_ => RemoveApiKey());
        ClearCacheCommand = new RelayCommand(_ => ClearCache());
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());

        _apiKey = ApiKeyStore.Load();
        RefreshKeyStatus();
        RefreshCache();
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            SetProperty(ref _apiKey, value, nameof(ApiKey));
            KeyStatus = "Unsaved changes.";
        }
    }

    public string KeyStatus
    {
        get => _keyStatus;
        set => SetProperty(ref _keyStatus, value, nameof(KeyStatus));
    }

    public string CacheStatus
    {
        get => _cacheStatus;
        set => SetProperty(ref _cacheStatus, value, nameof(CacheStatus));
    }

    public int CacheFileCount
    {
        get => _cacheFileCount;
        set => SetProperty(ref _cacheFileCount, value, nameof(CacheFileCount));
    }

    public long CacheSizeBytes
    {
        get => _cacheSizeBytes;
        set => SetProperty(ref _cacheSizeBytes, value, nameof(CacheSizeBytes));
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value, nameof(Status));
    }

    public string LibraryPathText => $"Library: {Path.Combine(LibraryDirectory, "library.json")}";
    public string SchedulesPathText => $"Schedules: {Path.Combine(DataRoot, "schedules", "schedules.json")}";
    public string LogPathText => $"Error log: {Path.Combine(LogDirectory, "errors.log")}";
    public string VersionInfo => "Version 0.1.0 · preview build";
    public string GitHubUrl => "https://github.com/DetectiveLon/oriole-wallpaper-manager";

    public ICommand SaveApiKeyCommand { get; }
    public ICommand RemoveApiKeyCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    /// <summary>Raised when the stored key is removed so the view can clear its PasswordBox.</summary>
    public event Action? ApiKeyCleared;

    private void SaveApiKey()
    {
        try
        {
            ApiKeyStore.Save(ApiKey);
            RefreshKeyStatus();
            Status = "API key saved.";
        }
        catch (Exception ex)
        {
            Status = $"Could not save API key: {ex.Message}";
        }
    }

    private void RemoveApiKey()
    {
        ApiKeyStore.Clear();
        SetProperty(ref _apiKey, string.Empty, nameof(ApiKey));
        ApiKeyCleared?.Invoke();
        RefreshKeyStatus();
        Status = "Stored API key removed.";
    }

    private void ClearCache()
    {
        try
        {
            var files = EnumerateCacheFiles();
            foreach (var file in files)
                File.Delete(file);
            RefreshCache();
            Status = $"Removed {files.Length} cached file(s).";
        }
        catch (Exception ex)
        {
            Status = $"Could not clear cache: {ex.Message}";
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            Process.Start(new ProcessStartInfo { FileName = DataRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Could not open folder: {ex.Message}";
        }
    }

    private void RefreshKeyStatus()
    {
        KeyStatus = ApiKeyStore.HasKey
            ? "A key is stored for this Windows user."
            : "No key stored. SFW search works without one.";
    }

    private void RefreshCache()
    {
        try
        {
            var files = EnumerateCacheFiles();
            var bytes = files.Sum(file =>
            {
                try { return new FileInfo(file).Length; }
                catch { return 0L; }
            });

            CacheFileCount = files.Length;
            CacheSizeBytes = bytes;
            CacheStatus = $"{files.Length} cached file(s), {bytes / 1024.0 / 1024.0:F1} MB.";
        }
        catch (Exception ex)
        {
            CacheStatus = $"Could not read cache: {ex.Message}";
        }
    }

    private static string[] EnumerateCacheFiles() =>
        Directory.Exists(CacheDirectory)
            ? Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories).ToArray()
            : [];
}
