using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
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
        _isFavorite = entry.IsFavorite;
        DetailText = $"{entry.Width:N0} × {entry.Height:N0} · {entry.Category} · {entry.Collection}";
        SelectedCollection = string.IsNullOrWhiteSpace(entry.Collection) ? "Unsorted" : entry.Collection;

        // Tags and colours are formatted defensively: the store may model them as a
        // delimited string or as a collection, and both render the same way here.
        UpdateTags(entry.Tags);
        ColorsText = TextOf(entry.Colors);
        ColorSwatches = BuildSwatches(ColorsText);
    }

    public string Id { get; }

    public string Title { get; }

    public string LocalPath { get; }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
                OnPropertyChanged(nameof(FavoriteLabel));
        }
    }

    public string FavoriteLabel => IsFavorite ? "Unfavorite" : "Favorite";

    public string DetailText { get; }

    private string _tagsText = string.Empty;
    public string TagsText
    {
        get => _tagsText;
        private set => SetProperty(ref _tagsText, value);
    }

    private IReadOnlyList<string> _tagChips = Array.Empty<string>();
    /// <summary>Individual tags split from TagsText, shown as read-only chips on the card.</summary>
    public IReadOnlyList<string> TagChips
    {
        get => _tagChips;
        private set
        {
            if (SetProperty(ref _tagChips, value))
                OnPropertyChanged(nameof(HasTags));
        }
    }

    public bool HasTags => TagChips.Count > 0;

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

    /// <summary>Rebuilds the chips and editor text from the value just written to the store.</summary>
    public void UpdateTags(object? tags)
    {
        TagsText = TextOf(tags);
        TagChips = BuildTagChips(TagsText);
        TagText = TagsText;
    }

    /// <summary>Shared with the tag filter so chips and filtering tokenize tags identically.</summary>
    public static IReadOnlyList<string> BuildTagChips(string tags) =>
        tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
    // One gate for every read and every mutation+reload chain. Reads that arrive
    // while another reload is running are coalesced: only the newest request may
    // publish its snapshot, so an older slow read cannot paint stale metadata.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _reloadVersion;
    private int _appliedReloadVersion;

    // Collections created before any wallpaper is assigned have no entry in the index;
    // keep them for this session and merge them with entry-derived names on reload.
    private readonly HashSet<string> _emptyCollections = new(StringComparer.OrdinalIgnoreCase);

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
        ClearTagFilterCommand = new AsyncRelayCommand(
            _ => { TagFilterText = string.Empty; return Task.CompletedTask; },
            ex => Status = $"Could not clear the tag filter: {ex.Message}");
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

    public ICommand ClearTagFilterCommand { get; }

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    private DisplayMonitor? _selectedDisplay;
    public DisplayMonitor? SelectedDisplay
    {
        get => _selectedDisplay;
        set
        {
            if (SetProperty(ref _selectedDisplay, value))
                // Fingerprint, not the volatile COM path, survives a monitor replug.
                MonitorSelectionService.Instance.Select(value?.Fingerprint);
        }
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

    private string _tagFilterText = string.Empty;
    /// <summary>Free-text tag filter. Separate several tags with commas to match any of them.</summary>
    public string TagFilterText
    {
        get => _tagFilterText;
        set
        {
            if (SetProperty(ref _tagFilterText, value))
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

    private string _emptyStateTitle = "Your library is waiting";
    public string EmptyStateTitle
    {
        get => _emptyStateTitle;
        private set => SetProperty(ref _emptyStateTitle, value);
    }

    private string _displayDetectionError = string.Empty;
    /// <summary>Detection failures stay visible independently of the frequently
    /// overwritten library status line.</summary>
    public string DisplayDetectionError
    {
        get => _displayDetectionError;
        private set
        {
            if (SetProperty(ref _displayDetectionError, value))
                OnPropertyChanged(nameof(HasDisplayDetectionError));
        }
    }

    public bool HasDisplayDetectionError => !string.IsNullOrWhiteSpace(DisplayDetectionError);

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

            var primary = detection.Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? detection.Monitors.FirstOrDefault();
            // Resolve by stable fingerprint so a replugged panel (new COM path)
            // keeps the user's chosen apply target.
            MonitorSelectionService.Instance.Load(Displays.Select(monitor => monitor.Fingerprint));
            var targetFingerprint = MonitorSelectionService.Instance.Resolve(primary?.Fingerprint);
            SelectedDisplay = Displays.FirstOrDefault(monitor =>
                    string.Equals(monitor.Fingerprint, targetFingerprint, StringComparison.OrdinalIgnoreCase))
                ?? primary;
            // Keep this out of Status: RefreshCommand overwrites Status on view load,
            // which used to erase the detection error before anyone could read it.
            DisplayDetectionError = detection.Succeeded
                ? string.Empty
                : $"Display detection unavailable: {detection.Error}";
        }
        catch (Exception ex)
        {
            // A detection failure must never prevent the library commands from loading.
            DisplayDetectionError = $"Display detection unavailable: {ex.Message}";
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

    private async Task ReloadAsync(bool announce = true)
    {
        var version = Interlocked.Increment(ref _reloadVersion);
        await _reloadGate.WaitAsync();
        try
        {
            // A newer request already published a fresher snapshot while this one
            // waited; nothing from this read can be usefully shown.
            if (version <= Volatile.Read(ref _appliedReloadVersion))
                return;
            await ApplyReloadAsync(version, announce);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Runs a mutation together with the reload that shows it. Holding the gate for
    /// the whole chain means no read can start before the write commits or publish
    /// a snapshot taken in between.
    /// </summary>
    private async Task WithReloadAsync(Func<Task> mutation, bool announce = false)
    {
        await _reloadGate.WaitAsync();
        try
        {
            await mutation();
            await ApplyReloadAsync(Interlocked.Increment(ref _reloadVersion), announce);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task ApplyReloadAsync(int version, bool announce)
    {
        IsRefreshEnabled = false;
        try
        {
            if (announce)
                Status = "Loading library…";
            var entries = await _store.GetAllAsync();
            // Only the newest request is allowed to publish. Concurrent refreshes
            // therefore coalesce into the read started by the last request.
            if (version < Volatile.Read(ref _reloadVersion))
                return;
            _entries = entries;
            Volatile.Write(ref _appliedReloadVersion, version);
            RebuildCollections();
            RenderEntries();
            if (announce)
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
            .Concat(_emptyCollections)
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
        var tagQuery = TagFilterText.Trim();
        var tagTerms = tagQuery.Length == 0
            ? Array.Empty<string>()
            : tagQuery.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var collectionFilter = _selectedCollectionFilter;
        var filtered = _entries.Where(entry =>
                (!FavoritesOnly || entry.IsFavorite)
                && (string.Equals(collectionFilter, AllCollections, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Collection, collectionFilter, StringComparison.OrdinalIgnoreCase))
                && (query.Length == 0
                    || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Collection.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Tags.Contains(query, StringComparison.OrdinalIgnoreCase))
                && MatchesTagFilter(entry.Tags, tagTerms));

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

    /// <summary>
    /// Whole-term tag matching: the filter tokenizes exactly like the card chips so
    /// "art" no longer matches "cartoon".
    /// </summary>
    private static bool MatchesTagFilter(string tags, string[] terms)
    {
        if (terms.Length == 0)
            return true;
        var chips = LibraryEntryViewModel.BuildTagChips(tags);
        return terms.Any(term => chips.Any(chip =>
            string.Equals(chip, term, StringComparison.OrdinalIgnoreCase)));
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
            var libraryEmpty = _entries.Count == 0;
            EmptyStateTitle = libraryEmpty ? "Your library is waiting" : "No matches found";
            EmptyMessage = libraryEmpty
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

            // Apply the target display's saved fit: Windows has one global fit mode
            // and leaves it untouched when no position is supplied.
            var fit = DisplayPreferencesStore.Load().GetFit(monitor.Fingerprint);
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, entry.LocalPath, fit.ToWallpaperPosition());
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
        var applied = false;
        var next = false;
        try
        {
            await WithReloadAsync(async () =>
            {
                // Both clicks queue behind the reload gate. Reading the committed
                // value here (rather than the immutable card snapshot) means a second
                // click flips the value the first one wrote instead of repeating it.
                var stored = (await _store.GetAllAsync()).FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                if (stored is null)
                    return;
                next = !stored.IsFavorite;
                if (!await _store.SetFavoriteAsync(entry.Id, next))
                    return;
                applied = true;
                // Keep the clicked card in sync while the reload rebuilds the page.
                entry.IsFavorite = next;
            });
            if (!applied)
            {
                Status = "Could not update favorite: entry no longer exists.";
                return;
            }
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
        var removed = false;
        var fileDeleted = false;
        try
        {
            await WithReloadAsync(async () =>
            {
                (removed, fileDeleted) = await _store.DeleteEntryWithResultAsync(
                    entry.Id, deleteFile, CancellationToken.None);
                if (removed)
                    SelectedEntry = null;
            });
            IsRemoveConfirmationOpen = false;
            PendingRemoveEntry = null;
            DeleteFileOnRemove = false;
            if (!removed)
            {
                // Removed=false now covers two cases: the entry is gone, or an owned
                // image could not be deleted and the entry was kept for retry. The
                // index tells us which one happened, so report them differently.
                var remaining = await _store.GetAllAsync();
                var kept = remaining.Any(candidate =>
                    string.Equals(candidate.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                Status = kept
                    ? "Could not delete the image file; entry kept"
                    : "That library entry no longer exists.";
                return;
            }

            Status = deleteFile
                ? fileDeleted
                    ? $"Removed \"{entry.Title}\" from the library and deleted its image from Ori's folder."
                    : $"Removed \"{entry.Title}\" from the library. Its image was kept because it is outside Ori's folders."
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
        var changed = false;
        try
        {
            await WithReloadAsync(async () =>
            {
                changed = await _store.SetCollectionAsync(entry.Id, target);
                if (!changed)
                    return;
                // The collection is now anchored by an entry; it no longer needs the
                // session-only placeholder that kept it visible while empty.
                _emptyCollections.Remove(target);
            });
            // The reload rebuilds entries and collection lists, so a newly typed
            // collection is immediately available to the filter and every card.
            if (!changed)
            {
                Status = "That library entry no longer exists.";
                return;
            }
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
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, Unsorted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, AllCollections, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Enter a new collection name (other than 'Unsorted').";
            return;
        }
        if (CollectionFilters.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"A collection named \"{name}\" already exists.";
            return;
        }

        var selected = SelectedEntry;
        var changed = true;
        try
        {
            await WithReloadAsync(async () =>
            {
                if (selected is not null)
                {
                    changed = await _store.SetCollectionAsync(selected.Id, name);
                    if (!changed)
                        return;
                }
                else
                {
                    // No wallpaper is needed: an empty collection is a valid destination
                    // for a later Move. It is kept for this session (the index only knows
                    // collections that have at least one entry).
                    _emptyCollections.Add(name);
                }

                NewCollectionName = string.Empty;
            });
            if (!changed)
            {
                Status = "That library entry no longer exists.";
                return;
            }
            Status = selected is null
                ? $"Created empty collection \"{name}\". Move a wallpaper into it to keep it after a restart."
                : $"Created \"{name}\" and moved {selected.Title} into it.";
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
            var count = 0;
            await WithReloadAsync(async () =>
            {
                count = await _store.ImportFolderAsync(folderPath, progress, token);
            });
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
                : $"{found.Count} duplicate(s) found. One wallpaper per group was kept as the original and is not listed; removing a row is metadata-only and keeps the image file on disk.";
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
            var removed = false;
            await WithReloadAsync(async () =>
            {
                removed = await _store.DeleteEntryAsync(item.Id, false, CancellationToken.None);
                if (removed)
                    Duplicates.Remove(item);
            });
            OnPropertyChanged(nameof(HasDuplicates));
            DuplicateStatus = Duplicates.Count == 0
                ? "No duplicates remaining."
                : $"{Duplicates.Count} duplicate(s) remaining. Originals are kept; removal is metadata-only.";
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
        var changed = false;
        try
        {
            await WithReloadAsync(async () =>
            {
                changed = await _store.SetTagsAsync(card.Id, card.TagText ?? string.Empty, CancellationToken.None);
            });
            if (!changed)
            {
                card.TagStatus = "Entry no longer exists.";
                Status = "Could not update tags: entry no longer exists.";
                return;
            }

            // The reload rebuilt the cards from fresh store data, so update both the
            // clicked card and its replacement: the chips and an active tag filter
            // must reflect the tags that were just written.
            var stored = _entries.FirstOrDefault(entry =>
                string.Equals(entry.Id, card.Id, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                card.UpdateTags(stored.Tags);
                foreach (var replacement in Entries.Where(item =>
                             string.Equals(item.Id, card.Id, StringComparison.OrdinalIgnoreCase)
                             && !ReferenceEquals(item, card)))
                    replacement.UpdateTags(stored.Tags);
            }
            card.TagStatus = "Tags saved.";
            // Set after the reload: ReloadAsync owns Status while it runs.
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
