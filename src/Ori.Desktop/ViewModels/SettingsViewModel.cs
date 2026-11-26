using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Ori.Core.Services;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>View model for the Settings page: API key, cache, local data and about info.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ori");
    private static readonly string CacheDirectory = Path.Combine(DataRoot, "cache", "images");
    private static readonly string LibraryDirectory = Path.Combine(DataRoot, "library");
    private static readonly string LogDirectory = Path.Combine(DataRoot, "logs");
    private static readonly string WallpapersDirectory = Path.Combine(DataRoot, "Wallpapers");
    private static readonly string ErrorLogPath = Path.Combine(LogDirectory, "errors.log");

    // Log entries start with an ISO-8601 timestamp (see App.LogFailure).
    private static readonly Regex LogEntryPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", RegexOptions.Compiled);
    private static readonly Regex WindowsPathPattern = new(
        @"(?:[A-Za-z]:\\|\\\\)[^\s""<>|]+", RegexOptions.Compiled);

    private string _apiKey = string.Empty;
    private string _keyStatus = string.Empty;
    private string _cacheStatus = string.Empty;
    private string _status = string.Empty;
    private int _cacheFileCount;
    private long _cacheSizeBytes;
    private bool _startupEnabled;
    private string _startupToggleLabel = string.Empty;
    private string _startupStatus = string.Empty;
    private bool _isClearOrphansConfirmationOpen;
    private readonly StartupService _startup = new();

    public SettingsViewModel()
    {
        SaveApiKeyCommand = new RelayCommand(_ => SaveApiKey());
        RemoveApiKeyCommand = new RelayCommand(_ => RemoveApiKey());
        ClearCacheCommand = new RelayCommand(_ => ClearCache());
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        ToggleStartupCommand = new RelayCommand(_ => ToggleStartup());
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());
        ViewErrorLogCommand = new RelayCommand(_ => ViewErrorLog());
        ClearOrphansCommand = new RelayCommand(_ => OpenClearOrphansConfirmation());
        ConfirmClearOrphansCommand = new RelayCommand(_ => { _ = ClearOrphansAsync(); });
        CancelClearOrphansCommand = new RelayCommand(_ => IsClearOrphansConfirmationOpen = false);

        _startupEnabled = _startup.IsEnabled();
        _startupToggleLabel = _startupEnabled ? "Disable launch at login" : "Enable launch at login";
        _startupStatus = string.IsNullOrWhiteSpace(_startup.LastError)
            ? (_startupEnabled ? "Ori starts when you sign in." : "Ori only starts when you open it.")
            : $"Could not read startup setting: {_startup.LastError}";

        _apiKey = ApiKeyStore.Load();
        RefreshKeyStatus();
        RefreshCache();
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            // Views are cached and re-populate the box on every visit, which raises
            // PasswordChanged with the same value. Only a real edit is "unsaved".
            if (SetProperty(ref _apiKey, value, nameof(ApiKey)))
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

    public bool StartupEnabled
    {
        get => _startupEnabled;
        private set => SetProperty(ref _startupEnabled, value, nameof(StartupEnabled));
    }

    public string StartupToggleLabel
    {
        get => _startupToggleLabel;
        private set => SetProperty(ref _startupToggleLabel, value, nameof(StartupToggleLabel));
    }

    public string StartupStatus
    {
        get => _startupStatus;
        private set => SetProperty(ref _startupStatus, value, nameof(StartupStatus));
    }

    public bool IsClearOrphansConfirmationOpen
    {
        get => _isClearOrphansConfirmationOpen;
        private set => SetProperty(ref _isClearOrphansConfirmationOpen, value, nameof(IsClearOrphansConfirmationOpen));
    }

    public string ClearOrphansConfirmationText =>
        "Remove applied wallpaper files that are no longer in your library? Files still referenced by the library are kept.";

    public string LibraryPathText => $"Library: {Path.Combine(LibraryDirectory, "library.json")}";
    public string SchedulesPathText => $"Schedules: {Path.Combine(DataRoot, "schedules", "schedules.json")}";
    public string LogPathText => $"Error log: {ErrorLogPath}";
    public string VersionInfo => $"Version {ReadVersion()}";
    public string GitHubUrl => "https://github.com/DetectiveLon/ori-wallpaper-manager";

    public ICommand SaveApiKeyCommand { get; }
    public ICommand RemoveApiKeyCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ToggleStartupCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }
    public ICommand ViewErrorLogCommand { get; }
    public ICommand ClearOrphansCommand { get; }
    public ICommand ConfirmClearOrphansCommand { get; }
    public ICommand CancelClearOrphansCommand { get; }

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
        if (!ApiKeyStore.Clear())
        {
            RefreshKeyStatus();
            Status = "Could not remove the stored key. It may be locked by another program; try again.";
            return;
        }
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

    private void ToggleStartup()
    {
        _startup.Toggle();
        if (!string.IsNullOrWhiteSpace(_startup.LastError))
        {
            StartupStatus = $"Could not change startup setting: {_startup.LastError}";
            Status = StartupStatus;
            return;
        }

        StartupEnabled = _startup.IsEnabled();
        StartupToggleLabel = StartupEnabled ? "Disable launch at login" : "Enable launch at login";
        StartupStatus = StartupEnabled
            ? "Ori starts when you sign in."
            : "Ori only starts when you open it.";
        Status = StartupEnabled ? "Launch at login enabled." : "Launch at login disabled.";
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

    private void CopyDiagnostics()
    {
        try
        {
            var lastError = ReadLastErrorSummary();
            var summary = string.Join(Environment.NewLine,
                "Ori diagnostics",
                $"Version: {ReadVersion()}",
                $"OS: {RuntimeInformation.OSDescription}",
                $"Last error: {lastError}");
            Clipboard.SetText(summary);
            Status = "Redacted diagnostics copied to clipboard.";
        }
        catch (Exception ex)
        {
            Status = $"Could not copy diagnostics: {ex.Message}";
        }
    }

    private void ViewErrorLog()
    {
        if (!File.Exists(ErrorLogPath))
        {
            Status = "No error log has been written yet.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ErrorLogPath, UseShellExecute = true });
            Status = "Opened the error log.";
        }
        catch (Exception ex)
        {
            Status = $"Could not open the error log: {ex.Message}";
        }
    }

    private void OpenClearOrphansConfirmation()
    {
        IsClearOrphansConfirmationOpen = true;
        Status = "Review the confirmation below — nothing is deleted until you confirm.";
    }

    private async Task ClearOrphansAsync()
    {
        IsClearOrphansConfirmationOpen = false;
        if (!Directory.Exists(WallpapersDirectory))
        {
            Status = "There is no applied-wallpapers folder to clean.";
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(WallpapersDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
            if (files.Length == 0)
            {
                Status = "No applied wallpaper files to clean.";
                return;
            }

            var library = new WallpaperLibraryStore();
            var entries = await library.GetAllAsync().ConfigureAwait(true);

            // Only an unresolved path that could refer to one of the files about to be
            // deleted matters: after a data-folder move an entry can point at the old
            // root while the live file is right here. One unrelated stale import must
            // not disable cleanup forever.
            var wallpaperNames = new HashSet<string>(
                files.Select(file => Path.GetFileName(file) ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var unresolved = entries.Count(entry =>
                !string.IsNullOrWhiteSpace(entry.LocalPath)
                && !File.Exists(entry.LocalPath)
                && wallpaperNames.Contains(Path.GetFileName(entry.LocalPath)));
            if (unresolved > 0)
            {
                Status = $"Nothing deleted: {unresolved} library entr{(unresolved == 1 ? "y points" : "ies point")} at a missing copy of a file in this folder. Visit the Library to repair the paths, then try again.";
                return;
            }

            var referenced = new HashSet<string>(
                entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.LocalPath))
                    .Select(entry => Path.GetFullPath(entry.LocalPath)),
                StringComparer.OrdinalIgnoreCase);

            var removed = 0;
            foreach (var file in files)
            {
                if (referenced.Contains(Path.GetFullPath(file)))
                    continue;
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // A locked file is left in place; the status reports the partial result.
                }
            }
            Status = removed == 0
                ? "No orphan wallpaper files found; every file is still in your library."
                : $"Removed {removed} orphan wallpaper file(s).";
        }
        catch (Exception ex)
        {
            Status = $"Could not clean wallpaper files: {ex.Message}";
        }
    }

    /// <summary>Re-reads the stored key when the cached view is revisited.</summary>
    public void RefreshApiKey()
    {
        if (ApiKeyStore.TryLoad(out var key, out var error))
        {
            SetProperty(ref _apiKey, key, nameof(ApiKey));
            RefreshKeyStatus();
            return;
        }

        SetProperty(ref _apiKey, string.Empty, nameof(ApiKey));
        KeyStatus = $"A stored key could not be read ({error}). Enter your key again to replace it.";
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

    private static string ReadVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
                return "0.1.0 (fallback)";
            // Strip source-revision metadata (e.g. "1.2.3+abc123" -> "1.2.3").
            var plus = informational.IndexOf('+');
            var version = plus >= 0 ? informational[..plus] : informational;
            return version.Trim();
        }
        catch
        {
            return "0.1.0 (fallback)";
        }
    }

    private static string ReadLastErrorSummary()
    {
        try
        {
            if (!File.Exists(ErrorLogPath) || new FileInfo(ErrorLogPath).Length > 2 * 1024 * 1024)
                return "none recorded";
            var lines = File.ReadAllLines(ErrorLogPath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var match = LogEntryPattern.Match(lines[i]);
                if (!match.Success)
                    continue;
                // The first physical line of an entry is "<timestamp> <ExceptionType>: <message>".
                // Stack frames live on later lines, so this never includes a full stack.
                var summary = lines[i][match.Length..].Trim();
                return Redact(summary);
            }
            return "none recorded";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string Redact(string value) =>
        WindowsPathPattern.Replace(value, "<path>");
}
