using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ori.Core.Services;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// A single library card. Created fresh on every render so stale thumbnail decodes
/// can be discarded by the owning view model's generation counter.
/// </summary>
public sealed class LibraryEntryViewModel : ViewModelBase
{
    private static readonly Regex HexColorPattern = new("\\b[0-9a-fA-F]{6}\\b", RegexOptions.Compiled);

    public LibraryEntryViewModel(WallpaperLibraryEntry entry)
    {
        Id = entry.Id;
        Title = entry.Title;
        LocalPath = entry.LocalPath;
        IsFavorite = entry.IsFavorite;
        FavoriteLabel = entry.IsFavorite ? "Unfavorite" : "Favorite";
        DetailText = $"{entry.Width:N0} × {entry.Height:N0} · {entry.Category} · {entry.Collection}";
        SelectedCollection = string.IsNullOrWhiteSpace(entry.Collection) ? "Unsorted" : entry.Collection;

        // Tags and colours are formatted defensively: the store may model them as a
        // delimited string or as a collection, and both render the same way here.
        TagsText = TextOf(entry.Tags);
        TagText = TagsText;
        ColorsText = TextOf(entry.Colors);
        ColorSwatches = BuildSwatches(ColorsText);
    }

    public string Id { get; }

    public string Title { get; }

    public string LocalPath { get; }

    public bool IsFavorite { get; }

    public string FavoriteLabel { get; }

    public string DetailText { get; }

    public string TagsText { get; }

    public string ColorsText { get; }

    public bool HasColors => ColorSwatches.Count > 0 || !string.IsNullOrWhiteSpace(ColorsText);

    public IReadOnlyList<Brush> ColorSwatches { get; }

    private string _tagText = string.Empty;
    public string TagText
    {
        get => _tagText;
        set => SetProperty(ref _tagText, value);
    }

    private string _tagStatus = string.Empty;
    public string TagStatus
    {
        get => _tagStatus;
        set => SetProperty(ref _tagStatus, value);
    }

    private string _selectedCollection = "Unsorted";
    public string SelectedCollection
    {
        get => _selectedCollection;
        set => SetProperty(ref _selectedCollection, value);
    }

    // Set when this card is first realized so a recycled row only decodes it once.
    public bool ThumbnailRequested { get; set; }

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private static string TextOf(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        IEnumerable items => string.Join(", ", items.Cast<object?>().Where(item => item is not null)),
        _ => value.ToString() ?? string.Empty
    };

    private static IReadOnlyList<Brush> BuildSwatches(string colors)
    {
        var swatches = new List<Brush>();
        foreach (Match match in HexColorPattern.Matches(colors))
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + match.Value)!);
                brush.Freeze();
                swatches.Add(brush);
                if (swatches.Count == 8)
                    break;
            }
            catch
            {
                // An unrecognised token is simply not shown as a swatch.
            }
        }
        return swatches;
    }
}

/// <summary>One row in the "Find duplicates" results panel.</summary>
public sealed class DuplicateEntryViewModel
{
    public DuplicateEntryViewModel(string id, string path)
    {
        Id = id;
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Id { get; }
    public string Path { get; }
    public string Name { get; }
}

public sealed class LibraryViewModel : ViewModelBase
{
    private const int ThumbnailDecodeWidth = 480;

    // The grid shows at most this many cards at once. The filtered library is kept as
    // lightweight metadata only; card view models (and their decoded thumbnails) are
    // created for the current page alone, so memory stays bounded for any library size.
    private const int PageSize = 48;

    private static readonly TimeSpan RenderDebounce = TimeSpan.FromMilliseconds(180);
    private const string AllCollections = "All collections";
    private const string Unsorted = "Unsorted";

    private readonly WallpaperLibraryStore _store = new();
    private readonly Dispatcher _dispatcher;
    private IReadOnlyList<WallpaperLibraryEntry> _entries = Array.Empty<WallpaperLibraryEntry>();
    private IReadOnlyList<WallpaperLibraryEntry> _filtered = Array.Empty<WallpaperLibraryEntry>();
    private CancellationTokenSource? _renderDebounce;
    private CancellationTokenSource? _importCts;
    private int _renderGeneration;
    private int _currentPage;
    private int _totalPages = 1;

    public LibraryViewModel(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        RefreshCommand = new AsyncRelayCommand(_ => ReloadAsync(), ex => Status = $"Could not refresh: {ex.Message}");
        ApplyCommand = new AsyncRelayCommand(p => ApplyEntryAsync(p as LibraryEntryViewModel), ex => Status = $"Could not apply wallpaper: {ex.Message}");
        FavoriteCommand = new AsyncRelayCommand(p => ToggleFavoriteAsync(p as LibraryEntryViewModel), ex => Status = $"Could not update favorite: {ex.Message}");
        OpenCommand = new AsyncRelayCommand(p => OpenEntry(p as LibraryEntryViewModel), ex => Status = $"Could not open image: {ex.Message}");
        RemoveCommand = new AsyncRelayCommand(p => RemoveEntryAsync(p as LibraryEntryViewModel), ex => Status = $"Could not remove library entry: {ex.Message}");
        MoveToCollectionCommand = new AsyncRelayCommand(p => MoveToCollectionAsync(p as LibraryEntryViewModel), ex => Status = $"Could not move to collection: {ex.Message}");
        CreateCollectionCommand = new AsyncRelayCommand(_ => CreateCollectionAsync(), ex => Status = $"Could not create collection: {ex.Message}");
        ConfirmRemoveCommand = new AsyncRelayCommand(_ => ConfirmRemoveAsync(), ex => Status = $"Could not remove library entry: {ex.Message}");
        CancelRemoveCommand = new AsyncRelayCommand(
            _ => { CancelRemove(); return Task.CompletedTask; },
            ex => Status = $"Could not cancel removal: {ex.Message}");
        FindDuplicatesCommand = new AsyncRelayCommand(_ => FindDuplicatesAsync(), ex => Status = $"Could not find duplicates: {ex.Message}");
        RemoveDuplicateCommand = new AsyncRelayCommand(p => RemoveDuplicateAsync(p as DuplicateEntryViewModel), ex => Status = $"Could not remove duplicate: {ex.Message}");
        SetTagsCommand = new AsyncRelayCommand(p => SetTagsAsync(p as LibraryEntryViewModel), ex => Status = $"Could not update tags: {ex.Message}");
        PreviousPageCommand = new AsyncRelayCommand(
            _ => { GoPreviousPage(); return Task.CompletedTask; },
            ex => Status = $"Could not change page: {ex.Message}");
        NextPageCommand = new AsyncRelayCommand(
            _ => { GoNextPage(); return Task.CompletedTask; },
            ex => Status = $"Could not change page: {ex.Message}");

        LoadDisplays();
    }

    // Only the current page's cards live here.
    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = new();

    public ObservableCollection<DisplayMonitor> Displays { get; } = new();

    public ObservableCollection<string> CollectionFilters { get; } = new();

    public ObservableCollection<string> MoveTargets { get; } = new();

    public ObservableCollection<DuplicateEntryViewModel> Duplicates { get; } = new();

    public IReadOnlyList<string> SortOptions { get; } = new[]
    {
        "Date saved", "Title", "Resolution", "Collection", "Favourites"
    };

    public ICommand RefreshCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand FavoriteCommand { get; }

    public ICommand OpenCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveToCollectionCommand { get; }

    public ICommand CreateCollectionCommand { get; }

    public ICommand ConfirmRemoveCommand { get; }

    public ICommand CancelRemoveCommand { get; }

    public ICommand FindDuplicatesCommand { get; }

    public ICommand RemoveDuplicateCommand { get; }

    public ICommand SetTagsCommand { get; }

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

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

    private string _selectedCollectionFilter = AllCollections;
    public string SelectedCollectionFilter
    {
        get => _selectedCollectionFilter;
        set
        {
            if (SetProperty(ref _selectedCollectionFilter, value))
                ScheduleRender();
        }
    }

    private string _selectedSort = "Date saved";
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetProperty(ref _selectedSort, value))
                ScheduleRender();
        }
    }

    private LibraryEntryViewModel? _selectedEntry;
    public LibraryEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    private string _newCollectionName = string.Empty;
    public string NewCollectionName
    {
        get => _newCollectionName;
        set => SetProperty(ref _newCollectionName, value);
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

    private bool _isRemoveConfirmationOpen;
    public bool IsRemoveConfirmationOpen
    {
        get => _isRemoveConfirmationOpen;
        set => SetProperty(ref _isRemoveConfirmationOpen, value);
    }

    private LibraryEntryViewModel? _pendingRemoveEntry;
    public LibraryEntryViewModel? PendingRemoveEntry
    {
        get => _pendingRemoveEntry;
        set
        {
            if (SetProperty(ref _pendingRemoveEntry, value))
                OnPropertyChanged(nameof(RemoveConfirmationText));
        }
    }

    public string RemoveConfirmationText => PendingRemoveEntry is null
        ? "Remove this wallpaper from the library?"
        : $"Remove \"{PendingRemoveEntry.Title}\" from the library?";

    private bool _deleteFileOnRemove;
    public bool DeleteFileOnRemove
    {
        get => _deleteFileOnRemove;
        set => SetProperty(ref _deleteFileOnRemove, value);
    }

    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        set => SetProperty(ref _isImporting, value);
    }

    private string _importProgress = string.Empty;
    public string ImportProgress
    {
        get => _importProgress;
        set => SetProperty(ref _importProgress, value);
    }

    private string _duplicateStatus = string.Empty;
    public string DuplicateStatus
    {
        get => _duplicateStatus;
        set => SetProperty(ref _duplicateStatus, value);
    }

    public bool HasDuplicates => Duplicates.Count > 0;

    // Computed page state - raised explicitly by RefreshPageState().
    public int PageSizeValue => PageSize;
    public bool CanGoPrevious => _currentPage > 0;
    public bool CanGoNext => _currentPage < _totalPages - 1;
    public string PageLabel => $"Page {_currentPage + 1} of {_totalPages}";

    private void LoadDisplays()
    {
        Displays.Clear();
        try
        {
            var detection = DisplayDetection.Read();
            foreach (var monitor in detection.Monitors)
                Displays.Add(monitor);
            SelectedDisplay = detection.Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? detection.Monitors.FirstOrDefault();
            if (!detection.Succeeded)
                Status = $"Display detection unavailable: {detection.Error}";
        }
        catch (Exception ex)
        {
            // A detection failure must never prevent the library commands from loading.
            Status = $"Display detection unavailable: {ex.Message}";
        }
    }

    /// <summary>Called by the view on Unloaded: settles debounces, imports and any open dialog.</summary>
    public void CancelOperations()
    {
        _renderDebounce?.Cancel();
        _renderDebounce?.Dispose();
        _renderDebounce = null;
        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = null;
        if (IsRemoveConfirmationOpen)
            CancelRemove();
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
            {
                // A changed filter always starts from the first page.
                _currentPage = 0;
                RenderEntries();
            }
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
            RebuildCollections();
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

    private void RebuildCollections()
    {
        var names = _entries.Select(entry => entry.Collection)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Clearing the ComboBox's ItemsSource momentarily drops its SelectedItem;
        // with a TwoWay binding that writes null into the VM through the setter,
        // which schedules a debounced render that would reset _currentPage to 0
        // after ReloadAsync has already rendered. There is no other caller of
        // this method, so cancelling the pending render loses nothing.
        _renderDebounce?.Cancel();
        _renderDebounce?.Dispose();
        _renderDebounce = null;

        // Remember the user's choice and restore it from the VM side after
        // repopulating, without going through the setter (which reschedules).
        var previousFilter = string.IsNullOrWhiteSpace(_selectedCollectionFilter)
            ? AllCollections
            : _selectedCollectionFilter;

        CollectionFilters.Clear();
        CollectionFilters.Add(AllCollections);
        CollectionFilters.Add(Unsorted);
        foreach (var name in names.Where(name => !string.Equals(name, Unsorted, StringComparison.OrdinalIgnoreCase)))
            CollectionFilters.Add(name);

        MoveTargets.Clear();
        MoveTargets.Add(Unsorted);
        foreach (var name in names.Where(name => !string.Equals(name, Unsorted, StringComparison.OrdinalIgnoreCase)))
            MoveTargets.Add(name);

        var restored = CollectionFilters.Any(name => string.Equals(name, previousFilter, StringComparison.OrdinalIgnoreCase))
            ? previousFilter
            : AllCollections;
        if (!string.Equals(_selectedCollectionFilter, restored, StringComparison.OrdinalIgnoreCase))
        {
            _selectedCollectionFilter = restored;
            OnPropertyChanged(nameof(SelectedCollectionFilter));
        }
    }

    private void RenderEntries()
    {
        ++_renderGeneration;
        var query = SearchText.Trim();
        var collectionFilter = _selectedCollectionFilter;
        var filtered = _entries.Where(entry =>
                (!FavoritesOnly || entry.IsFavorite)
                && (string.Equals(collectionFilter, AllCollections, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Collection, collectionFilter, StringComparison.OrdinalIgnoreCase))
                && (query.Length == 0
                    || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Collection.Contains(query, StringComparison.OrdinalIgnoreCase)));

        // Sorting happens before paging so every page follows the selected order.
        _filtered = ApplySort(filtered).ToArray();

        _totalPages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize));
        if (_currentPage >= _totalPages)
            _currentPage = _totalPages - 1;
        if (_currentPage < 0)
            _currentPage = 0;

        RenderCurrentPage();
        RefreshPageState();
    }

    private IOrderedEnumerable<WallpaperLibraryEntry> ApplySort(IEnumerable<WallpaperLibraryEntry> source) =>
        SelectedSort switch
        {
            "Title" => source.OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase),
            "Resolution" => source.OrderByDescending(entry => (long)entry.Width * entry.Height)
                .ThenByDescending(entry => entry.Height),
            "Collection" => source.OrderBy(
                    entry => string.IsNullOrWhiteSpace(entry.Collection) ? Unsorted : entry.Collection,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase),
            "Favourites" => source.OrderByDescending(entry => entry.IsFavorite)
                .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderByDescending(entry => entry.SavedAtUtc)
        };

    // Build card view models for the current page only. Thumbnails are decoded lazily by
    // the view, so at most PageSize bitmaps are ever realized at once.
    private void RenderCurrentPage()
    {
        Entries.Clear();
        if (_filtered.Count == 0)
        {
            EmptyMessage = _entries.Count == 0
                ? "Your library is empty. Save wallpapers from Discover to keep them available offline."
                : "No saved wallpapers match this filter.";
            return;
        }

        foreach (var entry in _filtered.Skip(_currentPage * PageSize).Take(PageSize))
            Entries.Add(new LibraryEntryViewModel(entry));
    }

    private void GoPreviousPage()
    {
        if (_currentPage <= 0)
            return;
        --_currentPage;
        RenderCurrentPage();
        RefreshPageState();
    }

    private void GoNextPage()
    {
        if (_currentPage >= _totalPages - 1)
            return;
        ++_currentPage;
        RenderCurrentPage();
        RefreshPageState();
    }

    private void RefreshPageState()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
    }

    // Called from the view when a card is realized. Tying decode to realization means a
    // large library never creates a decoded bitmap for a card outside the current page.
    public void RequestThumbnail(LibraryEntryViewModel? card)
    {
        if (card is null || card.ThumbnailRequested || !File.Exists(card.LocalPath))
            return;
        card.ThumbnailRequested = true;
        _ = LoadThumbnailAsync(card, card.LocalPath, _renderGeneration);
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
            Status = $"Opened {entry.Title} in the image viewer.";
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
        {
            Status = "Could not update favorite: no wallpaper is selected.";
            return;
        }
        try
        {
            var next = !entry.IsFavorite;
            await _store.SetFavoriteAsync(entry.Id, next);
            await ReloadAsync();
            Status = $"{entry.Title} {(next ? "added to" : "removed from")} favourites.";
        }
        catch (Exception ex)
        {
            Status = $"Could not update favorite: {ex.Message}";
        }
    }

    // Removal is a two-step flow now: this only opens the themed confirmation panel.
    // Nothing is deleted (metadata or file) until the user explicitly confirms.
    private Task RemoveEntryAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null)
        {
            Status = "Could not remove library entry: no wallpaper is selected.";
            return Task.CompletedTask;
        }

        PendingRemoveEntry = entry;
        DeleteFileOnRemove = false;
        IsRemoveConfirmationOpen = true;
        Status = $"Confirm removal of \"{entry.Title}\" — nothing has been deleted yet.";
        return Task.CompletedTask;
    }

    private void CancelRemove()
    {
        IsRemoveConfirmationOpen = false;
        PendingRemoveEntry = null;
        DeleteFileOnRemove = false;
        Status = "Removal cancelled. Nothing was deleted.";
    }

    private async Task ConfirmRemoveAsync()
    {
        var entry = PendingRemoveEntry;
        if (entry is null)
        {
            IsRemoveConfirmationOpen = false;
            Status = "Nothing to remove.";
            return;
        }

        var deleteFile = DeleteFileOnRemove;
        try
        {
            var removed = await _store.DeleteEntryAsync(entry.Id, deleteFile, CancellationToken.None);
            IsRemoveConfirmationOpen = false;
            PendingRemoveEntry = null;
            DeleteFileOnRemove = false;
            if (!removed)
            {
                Status = "That library entry no longer exists.";
                return;
            }

            SelectedEntry = null;
            await ReloadAsync();
            Status = deleteFile
                ? $"Removed \"{entry.Title}\" from the library and deleted its image from Ori's folder."
                : $"Removed \"{entry.Title}\" from the library (image file kept on disk).";
        }
        catch (Exception ex)
        {
            Status = $"Could not remove library entry: {ex.Message}";
        }
    }

    private async Task MoveToCollectionAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null)
        {
            Status = "Could not move to collection: no wallpaper is selected.";
            return;
        }

        var target = string.IsNullOrWhiteSpace(entry.SelectedCollection)
            ? Unsorted
            : entry.SelectedCollection.Trim();
        try
        {
            var changed = await _store.SetCollectionAsync(entry.Id, target);
            if (!changed)
            {
                Status = "That library entry no longer exists.";
                return;
            }

            // Reload entries and collection lists so a newly typed collection is
            // immediately available to the filter and every card.
            await ReloadAsync();
            Status = $"Moved {entry.Title} to {target}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not move to collection: {ex.Message}";
        }
    }

    private async Task CreateCollectionAsync()
    {
        var name = NewCollectionName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, Unsorted, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Enter a new collection name (other than 'Unsorted').";
            return;
        }
        if (SelectedEntry is null)
        {
            Status = "Select a wallpaper first, then choose New collection.";
            return;
        }

        var title = SelectedEntry.Title;
        try
        {
            var changed = await _store.SetCollectionAsync(SelectedEntry.Id, name);
            if (!changed)
            {
                Status = "That library entry no longer exists.";
                return;
            }

            NewCollectionName = string.Empty;
            await ReloadAsync();
            Status = $"Created \"{name}\" and moved {title} into it.";
        }
        catch (Exception ex)
        {
            Status = $"Could not create collection: {ex.Message}";
        }
    }

    /// <summary>Invoked by the view's folder picker; progress is reported inline.</summary>
    public async Task ImportFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Status = "No folder was selected.";
            return;
        }
        if (IsImporting)
        {
            Status = "An import is already running.";
            return;
        }

        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;
        var progress = new Progress<string>(message => ImportProgress = message);

        IsImporting = true;
        ImportProgress = "Starting import…";
        Status = $"Importing wallpapers from \"{folderPath}\"…";
        try
        {
            var count = await _store.ImportFolderAsync(folderPath, progress, token);
            await ReloadAsync();
            Status = $"Imported {count} wallpaper(s).";
            ImportProgress = $"Imported {count} wallpaper(s).";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = "Import cancelled.";
            ImportProgress = "Import cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Could not import folder: {ex.Message}";
            ImportProgress = "Import failed.";
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    private async Task FindDuplicatesAsync()
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            Status = "Scanning for duplicate wallpapers…";
            var found = await _store.FindDuplicatesAsync(cancellation.Token);
            Duplicates.Clear();
            foreach (var (id, path) in found)
                Duplicates.Add(new DuplicateEntryViewModel(id, path));
            OnPropertyChanged(nameof(HasDuplicates));

            DuplicateStatus = found.Count == 0
                ? "No duplicates found."
                : $"{found.Count} duplicate(s) found. Remove only the ones you want gone.";
            Status = DuplicateStatus;
        }
        catch (Exception ex)
        {
            Status = $"Could not find duplicates: {ex.Message}";
        }
    }

    private async Task RemoveDuplicateAsync(DuplicateEntryViewModel? item)
    {
        if (item is null)
            return;
        try
        {
            // Explicit, metadata-only removal: a duplicate row never deletes a file.
            var removed = await _store.DeleteEntryAsync(item.Id, false, CancellationToken.None);
            if (removed)
                Duplicates.Remove(item);
            OnPropertyChanged(nameof(HasDuplicates));
            await ReloadAsync();
            DuplicateStatus = $"{Duplicates.Count} duplicate(s) remaining.";
            Status = removed
                ? $"Removed duplicate \"{item.Name}\" from the library (file kept on disk)."
                : "That library entry no longer exists.";
        }
        catch (Exception ex)
        {
            Status = $"Could not remove duplicate: {ex.Message}";
        }
    }

    private async Task SetTagsAsync(LibraryEntryViewModel? card)
    {
        if (card is null)
        {
            Status = "Could not update tags: no wallpaper is selected.";
            return;
        }
        try
        {
            var changed = await _store.SetTagsAsync(card.Id, card.TagText ?? string.Empty, CancellationToken.None);
            if (!changed)
            {
                card.TagStatus = "Entry no longer exists.";
                Status = "Could not update tags: entry no longer exists.";
                return;
            }
            card.TagStatus = "Tags saved.";
            Status = $"Tags saved for {card.Title}.";
        }
        catch (Exception ex)
        {
            card.TagStatus = "Save failed.";
            Status = $"Could not update tags: {ex.Message}";
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
