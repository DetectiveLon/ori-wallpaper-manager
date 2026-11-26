using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ori.Core.Models;
using Ori.Core.Services;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>A label/value pair used by the filter combo boxes.</summary>
public sealed class FilterChoice
{
    public FilterChoice(string label, object value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public object Value { get; }
    public override string ToString() => Label;
}

/// <summary>A one-tap tag combination with sensible category/colour defaults.</summary>
public sealed class TagPreset
{
    public TagPreset(string label, string tags, bool general, bool anime, bool people, params string[] colors)
    {
        Label = label;
        Tags = tags;
        General = general;
        Anime = anime;
        People = people;
        Colors = colors;
    }

    public string Label { get; }
    public string Tags { get; }
    public bool General { get; }
    public bool Anime { get; }
    public bool People { get; }
    public IReadOnlyList<string> Colors { get; }
}

/// <summary>One selectable Wallhaven palette colour.</summary>
public sealed class ColorChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public ColorChoice(string hex)
    {
        Hex = hex;
        SwatchBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex)!);
        ToolTip = $"Filter by #{hex}";
    }

    public string Hex { get; }
    public Brush SwatchBrush { get; }
    public string ToolTip { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A search result card, including its lazily downloaded thumbnail.</summary>
public sealed class WallpaperCard : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;

    public WallpaperCard(WallhavenWallpaper wallpaper) => Wallpaper = wallpaper;

    public WallhavenWallpaper Wallpaper { get; }
    public string Resolution => $"{Wallpaper.Width:N0} × {Wallpaper.Height:N0}";
    public string Meta => $"{Wallpaper.Category} · {Wallpaper.Purity} · {Wallpaper.FileType}";

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Wallhaven discovery and direct-to-display wallpaper selection (MVVM port of DiscoverPage).</summary>
public sealed class DiscoverViewModel : ViewModelBase
{
    private static readonly WallhavenClient Api = new(WallhavenClient.CreateHttpClient());
    private static readonly WallhavenImageDownloader Images = new();
    private static readonly WallpaperImageCache LibraryImages = new();
    private static readonly WallpaperLibraryStore Library = new();

    private readonly DelegateCommand _searchCommand;
    private readonly DelegateCommand _previousCommand;
    private readonly DelegateCommand _nextCommand;
    private readonly DelegateCommand _previewCommand;
    private readonly DelegateCommand _saveCommand;
    private readonly DelegateCommand _applyCommand;
    private readonly DelegateCommand _applySelectedCommand;
    private readonly DelegateCommand _openPageCommand;
    private readonly DelegateCommand _applyPresetCommand;
    private readonly DelegateCommand _applyTagCommand;

    private WallhavenSearchPage? _currentPage;
    private WallhavenFilters? _currentFilters;
    private CancellationTokenSource? _searchCts;
    private bool _busy;
    private int _searchStarting;
    private string _status = "Choose filters and search.";
    private string _pageText = string.Empty;
    private WallpaperCard? _selectedCard;
    private DisplayMonitor? _selectedMonitor;

    // Filter values that the view also writes from code (presets, API-key refresh,
    // sorting enablement) need change notification so the controls stay in sync.
    private string _tags = string.Empty;
    private bool _general = true;
    private bool _anime = true;
    private bool _people = true;
    private FilterChoice? _selectedSorting;

    public DiscoverViewModel()
    {
        RecentTags = new ObservableCollection<string>(RecentTagsStore.Load());
        TagPresets = BuildTagPresets();

        SortingChoices =
        [
            new FilterChoice("Random", WallhavenSort.Random),
            new FilterChoice("Date added", WallhavenSort.DateAdded),
            new FilterChoice("Relevance", WallhavenSort.Relevance),
            new FilterChoice("Most viewed", WallhavenSort.Views),
            new FilterChoice("Most favourited", WallhavenSort.Favorites),
            new FilterChoice("Toplist", WallhavenSort.Toplist)
        ];
        SelectedSorting = ChoiceFor(SortingChoices, WallhavenSort.Random);

        TopRangeChoices =
        [
            new FilterChoice("Day", WallhavenTopRange.Day),
            new FilterChoice("3 days", WallhavenTopRange.ThreeDays),
            new FilterChoice("Week", WallhavenTopRange.Week),
            new FilterChoice("Month", WallhavenTopRange.Month),
            new FilterChoice("3 months", WallhavenTopRange.ThreeMonths),
            new FilterChoice("6 months", WallhavenTopRange.SixMonths),
            new FilterChoice("Year", WallhavenTopRange.Year)
        ];
        SelectedTopRange = ChoiceFor(TopRangeChoices, WallhavenTopRange.Month);

        OrderChoices =
        [
            new FilterChoice("Descending", WallhavenOrder.Descending),
            new FilterChoice("Ascending", WallhavenOrder.Ascending)
        ];
        SelectedOrder = ChoiceFor(OrderChoices, WallhavenOrder.Descending);

        RatioChoices =
        [
            new FilterChoice("Any ratio", string.Empty),
            new FilterChoice("Landscape · 16:9", "16x9"),
            new FilterChoice("Landscape · 16:10", "16x10"),
            new FilterChoice("Ultrawide · 21:9", "21x9"),
            new FilterChoice("Ultrawide · 32:9", "32x9"),
            new FilterChoice("Super ultrawide · 48:9", "48x9"),
            new FilterChoice("Portrait · 9:16", "9x16"),
            new FilterChoice("Portrait · 10:16", "10x16"),
            new FilterChoice("Portrait · 9:18", "9x18"),
            new FilterChoice("Classic · 3:2", "3x2"),
            new FilterChoice("Classic · 4:3", "4x3"),
            new FilterChoice("Classic · 5:4", "5x4"),
            new FilterChoice("Square · 1:1", "1x1")
        ];
        SelectedRatio = ChoiceFor(RatioChoices, string.Empty);

        ColorChoices = new ObservableCollection<ColorChoice>(
            WallhavenColors.Allowed
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => new ColorChoice(value)));

        try
        {
            ApiKey = ApiKeyStore.Load();
        }
        catch
        {
            ApiKey = string.Empty;
        }

        _searchCommand = new DelegateCommand(_ => SearchFirstPageAsync(), _ => !_busy, ReportCommandFailure);
        _previousCommand = new DelegateCommand(_ => ChangePageAsync(-1), _ => CanGoTo(-1), ReportCommandFailure);
        _nextCommand = new DelegateCommand(_ => ChangePageAsync(1), _ => CanGoTo(1), ReportCommandFailure);
        _previewCommand = new DelegateCommand(card => PreviewWallpaperAsync(card as WallpaperCard),
            card => !_busy && card is WallpaperCard, ReportCommandFailure);
        _saveCommand = new DelegateCommand(card => SaveWallpaperAsync(card as WallpaperCard),
            card => !_busy && card is WallpaperCard, ReportCommandFailure);
        // A missing display must not disable this button forever: ApplyWallpaperAsync
        // already reports a friendly status when no monitor is selected.
        _applyCommand = new DelegateCommand(card => ApplyWallpaperAsync(card as WallpaperCard),
            card => !_busy && card is WallpaperCard, ReportCommandFailure);
        // Keyboard-accessible counterpart: applies the highlighted result card.
        _applySelectedCommand = new DelegateCommand(_ => ApplyWallpaperAsync(SelectedCard),
            _ => !_busy && SelectedCard is not null, ReportCommandFailure);
        _openPageCommand = new DelegateCommand(card => OpenWallpaperPage(card as WallpaperCard),
            card => !_busy && card is WallpaperCard, ReportCommandFailure);
        _applyPresetCommand = new DelegateCommand(preset =>
            {
                ApplyTagPreset(preset as TagPreset);
                return Task.CompletedTask;
            },
            preset => preset is TagPreset, ReportCommandFailure);
        _applyTagCommand = new DelegateCommand(tag =>
            {
                ApplyRecentTag(tag as string);
                return Task.CompletedTask;
            },
            tag => tag is string, ReportCommandFailure);
    }

    // Filter state. Most values are plain auto-properties (two-way bindings write
    // directly into them); the ones code also changes notify so the UI follows.
    public string Tags
    {
        get => _tags;
        set => SetProperty(ref _tags, value);
    }
    public string MinimumResolution { get; set; } = string.Empty;
    public string ExactResolution { get; set; } = string.Empty;
    public string CustomColors { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool General
    {
        get => _general;
        set => SetProperty(ref _general, value);
    }
    public bool Anime
    {
        get => _anime;
        set => SetProperty(ref _anime, value);
    }
    public bool People
    {
        get => _people;
        set => SetProperty(ref _people, value);
    }
    public bool Safe { get; set; } = true;
    public bool Sketchy { get; set; }
    public bool Explicit { get; set; }

    public IReadOnlyList<FilterChoice> SortingChoices { get; }
    public IReadOnlyList<FilterChoice> TopRangeChoices { get; }
    public IReadOnlyList<FilterChoice> OrderChoices { get; }
    public IReadOnlyList<FilterChoice> RatioChoices { get; }
    public FilterChoice? SelectedSorting
    {
        get => _selectedSorting;
        set
        {
            if (SetProperty(ref _selectedSorting, value))
            {
                OnPropertyChanged(nameof(IsTopRangeEnabled));
                OnPropertyChanged(nameof(TopRangeToolTip));
            }
        }
    }
    public FilterChoice? SelectedTopRange { get; set; }
    public FilterChoice? SelectedOrder { get; set; }
    public FilterChoice? SelectedRatio { get; set; }
    public ObservableCollection<ColorChoice> ColorChoices { get; }

    /// <summary>The Toplist period is only meaningful when Toplist sorting is chosen.</summary>
    public bool IsTopRangeEnabled =>
        ChoiceValue(SelectedSorting, WallhavenSort.Random) == WallhavenSort.Toplist;

    public string TopRangeToolTip => IsTopRangeEnabled
        ? "Choose the period for Toplist results."
        : "Choose Toplist sorting to use this";

    /// <summary>Preset tag chips offered in the filter rail.</summary>
    public IReadOnlyList<TagPreset> TagPresets { get; }

    /// <summary>Recently used tag queries, newest first, persisted by RecentTagsStore.</summary>
    public ObservableCollection<string> RecentTags { get; }

    public ObservableCollection<DisplayMonitor> Monitors { get; } = [];
    public ObservableCollection<WallpaperCard> Results { get; } = [];

    public DisplayMonitor? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (SetProperty(ref _selectedMonitor, value))
                RefreshCommands();
        }
    }

    public WallpaperCard? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (SetProperty(ref _selectedCard, value))
                RefreshCommands();
        }
    }

    /// <summary>Window used as the owner of the full-size preview dialog; set by the view.</summary>
    public Window? OwnerWindow { get; set; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PageText
    {
        get => _pageText;
        private set => SetProperty(ref _pageText, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    public ICommand SearchCommand => _searchCommand;
    public ICommand PreviousCommand => _previousCommand;
    public ICommand NextCommand => _nextCommand;
    public ICommand PreviewCommand => _previewCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand ApplyCommand => _applyCommand;
    public ICommand ApplySelectedCommand => _applySelectedCommand;
    public ICommand OpenPageCommand => _openPageCommand;
    public ICommand ApplyPresetCommand => _applyPresetCommand;
    public ICommand ApplyTagCommand => _applyTagCommand;

    /// <summary>Enumerates displays once the view is loaded; detection failures stay non-fatal.</summary>
    public void LoadDisplays()
    {
        try
        {
            var detection = DisplayDetection.Read();
            Monitors.Clear();
            foreach (var monitor in detection.Monitors)
                Monitors.Add(monitor);
            SelectedMonitor = detection.Monitors.FirstOrDefault(m => m.IsPrimary)
                ?? detection.Monitors.FirstOrDefault();

            if (!detection.Succeeded)
                Status = $"Display detection unavailable: {detection.Error}";
            else if (Monitors.Count == 0)
                Status = "No connected display detected. You can still browse wallpapers.";
        }
        catch (Exception ex)
        {
            Status = $"Display detection unavailable: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-reads the stored key on every visit. Settings owns saving/removing the key;
    /// Discover must never resurrect a key that was removed there.
    /// </summary>
    public void RefreshApiKey()
    {
        try
        {
            ApiKey = ApiKeyStore.Load();
        }
        catch
        {
            ApiKey = string.Empty;
        }
    }

    /// <summary>Cancels any in-flight search; called when the view unloads.</summary>
    public void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    /// <summary>
    /// Last-resort handler for a command whose own operation threw outside its try block.
    /// Releases the busy latch so no control stays disabled after a failure.
    /// </summary>
    private void ReportCommandFailure(Exception exception)
    {
        IsBusy = false;
        Volatile.Write(ref _searchStarting, 0);
        OnUiThread(() =>
        {
            Status = $"Could not complete the action: {exception.Message}";
            RefreshCommands();
        });
    }

    private bool CanGoTo(int delta)
    {
        if (_busy || _currentPage is null)
            return false;
        var target = _currentPage.CurrentPage + delta;
        return target >= 1 && target <= _currentPage.LastPage;
    }

    private void RefreshCommands()
    {
        _searchCommand.RaiseCanExecuteChanged();
        _previousCommand.RaiseCanExecuteChanged();
        _nextCommand.RaiseCanExecuteChanged();
        _previewCommand.RaiseCanExecuteChanged();
        _saveCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _applySelectedCommand.RaiseCanExecuteChanged();
        _openPageCommand.RaiseCanExecuteChanged();
    }

    private async Task SearchFirstPageAsync()
    {
        if (_busy || Interlocked.Exchange(ref _searchStarting, 1) != 0)
            return;

        try
        {
            WallhavenFilters filters;
            try
            {
                filters = BuildFilters();
            }
            catch (Exception ex)
            {
                Status = ex.Message;
                return;
            }

            _currentFilters = filters;
            var succeeded = await SearchPageAsync(1, null);
            if (succeeded)
            {
                // Remember the query and persist the exact filters for automatic rotation.
                // Settings is the single source of truth for the API key; searching never saves it.
                RecordTags(filters.Query);
                try
                {
                    WallhavenFeedStore.Save(filters, 60);
                }
                catch
                {
                    // Feed persistence is a convenience; it must not fail a completed search.
                }
            }
        }
        finally
        {
            // Release the guard even if a binder or status update throws, otherwise
            // every subsequent Search click would be ignored forever.
            Volatile.Write(ref _searchStarting, 0);
        }
    }

    private WallhavenFilters BuildFilters()
    {
        var categories = WallhavenCategory.None;
        if (General) categories |= WallhavenCategory.General;
        if (Anime) categories |= WallhavenCategory.Anime;
        if (People) categories |= WallhavenCategory.People;

        var purity = WallhavenPurity.None;
        if (Safe) purity |= WallhavenPurity.Safe;
        if (Sketchy) purity |= WallhavenPurity.Sketchy;
        if (Explicit) purity |= WallhavenPurity.Explicit;
        if (purity.HasFlag(WallhavenPurity.Explicit) && string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Enter your Wallhaven API key to include NSFW results.");

        var ratio = ChoiceValue(SelectedRatio, string.Empty);
        var customColors = CustomColors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim().TrimStart('#'));
        var colors = ColorChoices.Where(item => item.IsSelected)
            .Select(item => item.Hex)
            .Concat(customColors)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filters = new WallhavenFilters
        {
            Query = NormalizeTags(Tags),
            Categories = categories,
            Purity = purity,
            Sorting = ChoiceValue(SelectedSorting, WallhavenSort.Random),
            Order = ChoiceValue(SelectedOrder, WallhavenOrder.Descending),
            TopRange = ChoiceValue(SelectedTopRange, WallhavenTopRange.Month),
            MinimumResolution = string.IsNullOrWhiteSpace(MinimumResolution) ? null : MinimumResolution.Trim(),
            ExactResolution = string.IsNullOrWhiteSpace(ExactResolution) ? null : ExactResolution.Trim(),
            Ratio = string.IsNullOrWhiteSpace(ratio) ? null : ratio,
            Colors = colors
        };
        filters.Validate();
        return filters;
    }

    private static string NormalizeTags(string value)
    {
        var input = value.Trim();
        if (!input.Contains(','))
            return input;
        return string.Join(' ', input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.StartsWith('-') || tag.StartsWith('+') ? tag : "+" + tag));
    }

    private static IReadOnlyList<TagPreset> BuildTagPresets() =>
    [
        new TagPreset("Nature", "nature, landscape", true, false, false, "77cc33", "669900"),
        new TagPreset("Minimal", "minimal, minimalism", true, false, false, "ffffff", "cccccc", "000000"),
        new TagPreset("Cyberpunk", "cyberpunk, neon", true, true, false, "993399", "0099cc", "ea4c88"),
        new TagPreset("Abstract", "abstract", true, true, false, "663399", "ff6600", "333399"),
        new TagPreset("Space", "space, galaxy, stars", true, false, false, "000000", "333399", "424153")
    ];

    private void ApplyTagPreset(TagPreset? preset)
    {
        if (preset is null)
            return;
        Tags = preset.Tags;
        General = preset.General;
        Anime = preset.Anime;
        People = preset.People;
        ApplyColors(preset.Colors);
        Status = $"Preset “{preset.Label}” applied — search to see results.";
    }

    private void ApplyRecentTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;
        Tags = tag;
        Status = $"Tag “{tag}” applied — search to see results.";
    }

    private void ApplyColors(IReadOnlyList<string> colors)
    {
        var selected = new HashSet<string>(colors, StringComparer.OrdinalIgnoreCase);
        foreach (var choice in ColorChoices)
            choice.IsSelected = selected.Contains(choice.Hex);
    }

    private void RecordTags(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;
        var normalized = query.Trim();
        if (RecentTags.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase))
            is string existing)
            RecentTags.Remove(existing);
        RecentTags.Insert(0, normalized);
        while (RecentTags.Count > RecentTagsStore.MaximumTags)
            RecentTags.RemoveAt(RecentTags.Count - 1);
        try
        {
            RecentTagsStore.Save(RecentTags);
        }
        catch
        {
            // Remembering tags is a convenience; never fail a search over it.
        }
    }

    private async Task ChangePageAsync(int delta)
    {
        if (!CanGoTo(delta))
            return;
        var target = _currentPage!.CurrentPage + delta;
        if (target < 1 || target > _currentPage.LastPage)
            return;
        await SearchPageAsync(target, _currentPage.Seed);
    }

    private async Task<bool> SearchPageAsync(int page, string? seed)
    {
        if (_currentFilters is null || _busy)
            return false;

        CancelSearch();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        IsBusy = true;
        RefreshCommands();
        Results.Clear();
        SelectedCard = null;
        PageText = string.Empty;
        Status = "Searching Wallhaven…";

        var succeeded = false;
        try
        {
            var filters = _currentFilters with { Page = page, Seed = seed };
            var result = await Api.SearchAsync(filters, ApiKey, token);
            _currentPage = result;
            _currentFilters = filters with { Seed = result.Seed };
            foreach (var wallpaper in result.Items)
                Results.Add(new WallpaperCard(wallpaper));

            Status = result.Items.Count == 0
                ? "No wallpapers matched. Try removing a filter."
                : $"Found {result.Items.Count} wallpapers.";
            PageText = $"Page {result.CurrentPage} of {result.LastPage}";
            await LoadPreviewsAsync(Results.ToList(), token);
            succeeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A navigation change or unload cancels the in-flight request.
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CancelSearch();
            RefreshCommands();
        }
        return succeeded;
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<WallpaperCard> cards, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = cards.Select(async card =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var download = await Images.DownloadAsync(
                    card.Wallpaper.ThumbnailUrl,
                    WallhavenImageDownloader.MaximumThumbnailBytes,
                    cancellationToken).ConfigureAwait(false);
                var bitmap = DecodeImage(download.Bytes, 520);
                OnUiThread(() => card.Thumbnail = bitmap);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One unavailable preview should not hide the rest of the search results.
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task SaveWallpaperAsync(WallpaperCard? card)
    {
        if (card is null)
            return;
        var wallpaper = card.Wallpaper;
        IsBusy = true;
        RefreshCommands();
        Status = $"Saving wallpaper #{wallpaper.Id} to your library…";
        try
        {
            var path = await LibraryImages.SaveToLibraryAsync(wallpaper);
            await Library.UpsertAsync(new WallpaperLibraryEntry
            {
                Id = wallpaper.Id,
                Title = $"Wallhaven #{wallpaper.Id}",
                PageUrl = wallpaper.PageUrl.ToString(),
                ImageUrl = wallpaper.ImageUrl.ToString(),
                LocalPath = path,
                Category = wallpaper.Category,
                Purity = wallpaper.Purity,
                Width = wallpaper.Width,
                Height = wallpaper.Height,
                Ratio = wallpaper.Ratio,
                SavedAtUtc = DateTimeOffset.UtcNow,
                Collection = "Unsorted"
            });
            Status = $"Wallpaper #{wallpaper.Id} saved to your local library.";
        }
        catch (Exception ex)
        {
            Status = $"Could not save wallpaper #{wallpaper.Id}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private async Task PreviewWallpaperAsync(WallpaperCard? card)
    {
        if (card is null)
            return;
        var wallpaper = card.Wallpaper;
        IsBusy = true;
        RefreshCommands();
        Status = $"Loading full preview for wallpaper #{wallpaper.Id}…";
        try
        {
            var path = await LibraryImages.DownloadOriginalAsync(wallpaper);
            var previewImage = new Image
            {
                Source = LoadBitmap(path, 1800),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var previewWindow = new Window
            {
                Title = $"Ori preview · #{wallpaper.Id}",
                Owner = OwnerWindow,
                Width = 1050,
                Height = 760,
                MinWidth = 720,
                MinHeight = 520,
                WindowStartupLocation = OwnerWindow is null
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.CenterOwner,
                Background = ResourceBrush("WindowBrush", "#10141B")
            };
            previewWindow.Content = new Border
            {
                Margin = new Thickness(12),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(10),
                Background = ResourceBrush("PanelBrush", "#1A222E"),
                Child = previewImage
            };
            Status = $"Previewing wallpaper #{wallpaper.Id}.";
            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            Status = $"Could not preview wallpaper #{wallpaper.Id}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private async Task ApplyWallpaperAsync(WallpaperCard? card)
    {
        if (card is null)
            return;
        if (SelectedMonitor is not DisplayMonitor monitor)
        {
            Status = "No target display is selected. Refresh the display list or reconnect a monitor.";
            return;
        }

        var wallpaper = card.Wallpaper;
        IsBusy = true;
        RefreshCommands();
        Status = $"Downloading wallpaper for {monitor.DeviceName}…";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var download = await Images.DownloadAsync(
                wallpaper.ImageUrl, WallhavenImageDownloader.MaximumWallpaperBytes, cancellation.Token);
            var extension = ValidateWallpaperImage(download.Bytes);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ori", "Wallpapers");
            Directory.CreateDirectory(directory);
            var id = string.Concat(wallpaper.Id.Where(char.IsAsciiLetterOrDigit));
            if (id.Length == 0)
                id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(directory, id + extension);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, download.Bytes, cancellation.Token);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, path);
            // Register the applied image in the library so it can be favourited,
            // collected and rotated (and so the file participates in cleanup).
            try
            {
                await Library.UpsertAsync(new WallpaperLibraryEntry
                {
                    Id = wallpaper.Id,
                    Title = $"Wallhaven #{wallpaper.Id}",
                    PageUrl = wallpaper.PageUrl.ToString(),
                    ImageUrl = wallpaper.ImageUrl.ToString(),
                    LocalPath = path,
                    Category = wallpaper.Category,
                    Purity = wallpaper.Purity,
                    Width = wallpaper.Width,
                    Height = wallpaper.Height,
                    Ratio = wallpaper.Ratio,
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Collection = "Unsorted"
                });
            }
            catch
            {
                // The wallpaper is already applied; a library index failure is non-fatal.
            }
            Status = $"Wallpaper applied to {monitor.DeviceName}. Saved to your library at {path}";
        }
        catch (Exception ex)
        {
            Status = $"Could not apply wallpaper: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private Task OpenWallpaperPage(WallpaperCard? card)
    {
        if (card is null)
            return Task.CompletedTask;
        var wallpaper = card.Wallpaper;
        if (!WallhavenImageDownloader.IsTrustedUri(wallpaper.PageUrl))
        {
            Status = "Wallhaven returned an unexpected page address.";
            return Task.CompletedTask;
        }
        try
        {
            Process.Start(new ProcessStartInfo(wallpaper.PageUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Could not open the browser: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    private static BitmapImage LoadBitmap(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource DecodeImage(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("The downloaded preview does not contain an image frame.");

        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            || frame.PixelWidth > 32_768 || frame.PixelHeight > 32_768
            || (long)frame.PixelWidth * frame.PixelHeight > 50_000_000)
        {
            throw new InvalidDataException("The thumbnail dimensions exceed Ori's image limits.");
        }

        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string ValidateWallpaperImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("The downloaded file does not contain an image frame.");

        const int maxDimension = 32_768;
        const long maxPixels = 50_000_000;
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            || frame.PixelWidth > maxDimension || frame.PixelHeight > maxDimension
            || (long)frame.PixelWidth * frame.PixelHeight > maxPixels)
            throw new InvalidDataException(
                $"Image dimensions {frame.PixelWidth:N0} × {frame.PixelHeight:N0} exceed Ori's decode limit.");

        var codec = decoder.CodecInfo.FriendlyName;
        if (codec.Contains("JPEG", StringComparison.OrdinalIgnoreCase)) return ".jpg";
        if (codec.Contains("PNG", StringComparison.OrdinalIgnoreCase)) return ".png";
        if (codec.Contains("BMP", StringComparison.OrdinalIgnoreCase)) return ".bmp";
        if (codec.Contains("TIFF", StringComparison.OrdinalIgnoreCase)) return ".tiff";

        throw new InvalidDataException($"Image format '{codec}' is not supported yet.");
    }

    private static FilterChoice ChoiceFor<T>(IReadOnlyList<FilterChoice> choices, T value) =>
        choices.First(choice => choice.Value is T candidate && EqualityComparer<T>.Default.Equals(candidate, value));

    private static T ChoiceValue<T>(FilterChoice? choice, T fallback) =>
        choice?.Value is T value ? value : fallback;

    private static Brush ResourceBrush(string key, string fallbackColor)
    {
        if (System.Windows.Application.Current?.Resources[key] is Brush brush)
            return brush;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackColor)!);
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>Minimal command implementation; re-queried explicitly when state changes.</summary>
    private sealed class DelegateCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool> _canExecute;
        private readonly Action<Exception> _onError;

        public DelegateCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute,
            Action<Exception>? onError = null)
        {
            _execute = execute;
            _canExecute = canExecute ?? (_ => true);
            _onError = onError ?? (_ => { });
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute(parameter);

        public async void Execute(object? parameter)
        {
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                // Never swallow: surface the failure and release the busy latch so
                // buttons cannot remain permanently disabled after an edge-case throw.
                _onError(ex);
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
