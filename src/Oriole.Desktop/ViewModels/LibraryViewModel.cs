using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Oriole.Core.Services;
using Oriole.Desktop.Mvvm;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.ViewModels;

/// <summary>
/// A single library card. Created fresh on every render so stale thumbnail decodes
/// can be discarded by the owning view model's generation counter.
/// </summary>
public sealed class LibraryEntryViewModel : ViewModelBase
{
    public LibraryEntryViewModel(WallpaperLibraryEntry entry)
    {
        Id = entry.Id;
        Title = entry.Title;
        LocalPath = entry.LocalPath;
        IsFavorite = entry.IsFavorite;
        FavoriteLabel = entry.IsFavorite ? "Unfavorite" : "Favorite";
        DetailText = $"{entry.Width:N0} × {entry.Height:N0} · {entry.Category} · {entry.Collection}";
    }

    public string Id { get; }

    public string Title { get; }

    public string LocalPath { get; }

    public bool IsFavorite { get; }

    public string FavoriteLabel { get; }

    public string DetailText { get; }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}

public sealed class LibraryViewModel : ViewModelBase
{
    private const int ThumbnailDecodeWidth = 480;
    private static readonly TimeSpan RenderDebounce = TimeSpan.FromMilliseconds(180);

    private readonly WallpaperLibraryStore _store = new();
    private readonly Dispatcher _dispatcher;
    private IReadOnlyList<WallpaperLibraryEntry> _entries = Array.Empty<WallpaperLibraryEntry>();
    private CancellationTokenSource? _renderDebounce;
    private int _renderGeneration;

    public LibraryViewModel(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        RefreshCommand = new AsyncRelayCommand(_ => ReloadAsync(), ex => Status = $"Could not refresh: {ex.Message}");
        ApplyCommand = new AsyncRelayCommand(p => ApplyEntryAsync(p as LibraryEntryViewModel), ex => Status = $"Could not apply wallpaper: {ex.Message}");
        FavoriteCommand = new AsyncRelayCommand(p => ToggleFavoriteAsync(p as LibraryEntryViewModel), ex => Status = $"Could not update favorite: {ex.Message}");
        OpenCommand = new AsyncRelayCommand(p => OpenEntry(p as LibraryEntryViewModel), ex => Status = $"Could not open image: {ex.Message}");
        RemoveCommand = new AsyncRelayCommand(p => RemoveEntryAsync(p as LibraryEntryViewModel), ex => Status = $"Could not remove library entry: {ex.Message}");

        LoadDisplays();
    }

    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = new();

    public ObservableCollection<DisplayMonitor> Displays { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand FavoriteCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand RemoveCommand { get; }

    private DisplayMonitor? _selectedDisplay;
    public DisplayMonitor? SelectedDisplay
    {
        get => _selectedDisplay;
        set => SetProperty(ref _selectedDisplay, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ScheduleRender();
        }
    }

    private bool _favoritesOnly;
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
                ScheduleRender();
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private string _emptyMessage = string.Empty;
    public string EmptyMessage
    {
        get => _emptyMessage;
        set => SetProperty(ref _emptyMessage, value);
    }

    private bool _isRefreshEnabled = true;
    public bool IsRefreshEnabled
    {
        get => _isRefreshEnabled;
        set => SetProperty(ref _isRefreshEnabled, value);
    }

    private void LoadDisplays()
    {
        var detection = DisplayDetection.Read();
        foreach (var monitor in detection.Monitors)
            Displays.Add(monitor);
        SelectedDisplay = detection.Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? detection.Monitors.FirstOrDefault();
        if (!detection.Succeeded)
            Status = $"Display detection unavailable: {detection.Error}";
    }

    private void ScheduleRender()
    {
        _renderDebounce?.Cancel();
        _renderDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        _renderDebounce = cancellation;
        _ = DebouncedRenderAsync(cancellation);
    }

    private async Task DebouncedRenderAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(RenderDebounce, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                RenderEntries();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = $"Could not filter the library: {ex.Message}";
        }
    }

    private async Task ReloadAsync()
    {
        IsRefreshEnabled = false;
        Status = "Loading library…";
        try
        {
            _entries = await _store.GetAllAsync();
            RenderEntries();
            Status = $"{_entries.Count} saved wallpaper(s).";
        }
        catch (Exception ex)
        {
            Status = $"Could not read the library: {ex.Message}";
        }
        finally
        {
            IsRefreshEnabled = true;
        }
    }

    private void RenderEntries()
    {
        var generation = ++_renderGeneration;
        var query = SearchText.Trim();
        var filtered = _entries.Where(entry =>
                (!FavoritesOnly || entry.IsFavorite)
                && (query.Length == 0
                    || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Collection.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Entries.Clear();
        if (filtered.Length == 0)
        {
            EmptyMessage = _entries.Count == 0
                ? "Your library is empty. Save wallpapers from Discover to keep them available offline."
                : "No saved wallpapers match this filter.";
            return;
        }

        foreach (var entry in filtered)
        {
            var card = new LibraryEntryViewModel(entry);
            Entries.Add(card);
            if (File.Exists(entry.LocalPath))
                _ = LoadThumbnailAsync(card, entry.LocalPath, generation);
        }
    }

    private Task ApplyEntryAsync(LibraryEntryViewModel? entry)
    {
        try
        {
            if (entry is null)
                throw new InvalidOperationException("No wallpaper is selected.");
            if (SelectedDisplay is not DisplayMonitor monitor)
                throw new InvalidOperationException("No display is selected.");
            if (!File.Exists(entry.LocalPath))
                throw new FileNotFoundException("The saved wallpaper file is missing.", entry.LocalPath);

            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, entry.LocalPath);
            Status = $"Applied {entry.Title} to {monitor.DeviceName}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not apply wallpaper: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    private Task OpenEntry(LibraryEntryViewModel? entry)
    {
        try
        {
            if (entry is null)
                throw new InvalidOperationException("No wallpaper is selected.");
            if (!File.Exists(entry.LocalPath))
                throw new FileNotFoundException("The image file is missing.", entry.LocalPath);
            Process.Start(new ProcessStartInfo(entry.LocalPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Could not open image: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    private async Task ToggleFavoriteAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null)
            return;
        try
        {
            await _store.SetFavoriteAsync(entry.Id, !entry.IsFavorite);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Status = $"Could not update favorite: {ex.Message}";
        }
    }

    private async Task RemoveEntryAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null)
            return;

        var answer = MessageBox.Show(
            Application.Current?.MainWindow,
            "Remove this item from the Oriole library? The downloaded image file will be kept on disk.",
            "Remove library entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await _store.RemoveAsync(entry.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Status = $"Could not remove library entry: {ex.Message}";
        }
    }

    private async Task LoadThumbnailAsync(LibraryEntryViewModel target, string path, int generation)
    {
        BitmapImage? bitmap = null;
        try
        {
            // Decode off the UI thread; the frozen bitmap is safe to assign on Dispatcher.
            bitmap = await Task.Run(() => LoadBitmap(path, ThumbnailDecodeWidth));
        }
        catch
        {
            // A missing or damaged local image does not prevent its library entry being managed.
        }

        if (bitmap is not null && generation == _renderGeneration)
            await _dispatcher.InvokeAsync(() => target.Thumbnail = bitmap);
    }

    private static BitmapImage LoadBitmap(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Fire-and-forget command with a catch-all so no fired task can tear down the app.
    /// Execute itself is synchronous void (never async void).
    /// </summary>
    private sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Action<Exception> _onError;

        public AsyncRelayCommand(Func<object?, Task> execute, Action<Exception> onError)
        {
            _execute = execute;
            _onError = onError;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _ = ExecuteInternalAsync(parameter);

        private async Task ExecuteInternalAsync(object? parameter)
        {
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                _onError(ex);
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
