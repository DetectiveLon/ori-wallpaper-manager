using System.Collections.ObjectModel;
using System.Windows.Input;
using Ori.Core.Models;
using Ori.Core.Services;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// One persisted Wallhaven rotation feed. The row is a projection of the Core feed
/// record and, at the same time, a complete <see cref="WallhavenFilters"/> editor:
/// every filter field is an editable draft and nothing is written until
/// <see cref="SaveCommand"/> validates and commits. Feed-level toggles (name,
/// cadence, enabled) keep their existing immediate-commit binding names.
/// </summary>
public sealed class FeedViewModel : ViewModelBase
{
    private readonly AutomationViewModel _owner;
    private readonly ObservableCollection<int> _cadenceOptions = new();
    private bool _suppressCommit;
    private bool _isDirty;

    private string _name = string.Empty;
    private int _cadenceMinutes = 60;
    private bool _isEnabled;
    private string _customCadenceText = string.Empty;
    private string _statusText = string.Empty;
    private string _saveError = string.Empty;

    // ----- filter draft (every WallhavenFilters field) -----
    private string _filterQuery = string.Empty;
    private WallhavenCategory _categories = WallhavenCategory.All;
    private WallhavenPurity _purity = WallhavenPurity.Safe;
    private WallhavenSort _sorting = WallhavenSort.Random;
    private WallhavenOrder _order = WallhavenOrder.Descending;
    private WallhavenTopRange _topRange = WallhavenTopRange.Month;
    private string _minimumResolution = string.Empty;
    private string _exactResolution = string.Empty;
    private string _ratio = string.Empty;
    private string _customColors = string.Empty;
    private int _page = 1;
    private string _seed = string.Empty;
    private int _maximumItemsPerFetch = RotationFeed.DefaultMaximumItemsPerFetch;
    private int _currentPage = 1;
    private int? _lastPage;

    public FeedViewModel(RotationFeed feed, AutomationViewModel owner)
    {
        _owner = owner;
        Id = feed.Id;

        // Every control delegates to the owner; Core persistence and the dispatcher
        // refresh both live there, so this row stays a thin binding surface.
        // ToggleCommand flips the CURRENT state: never call it from the IsEnabled
        // setter (see the comment there), or the new value is stored inverted.
        ToggleCommand = new RelayCommand(_ => CommitEnabled(!IsEnabled));
        RenameCommand = new RelayCommand(parameter => _owner.RenameFeed(Id, parameter as string ?? Name));
        SetCadenceCommand = new RelayCommand(parameter =>
            _owner.SetFeedCadence(Id, parameter is int minutes ? minutes : CadenceMinutes));
        ApplyCustomCadenceCommand = new RelayCommand(_ => ApplyCustomCadence());
        MoveUpCommand = new RelayCommand(_ => _owner.MoveFeedUp(Id));
        MoveDownCommand = new RelayCommand(_ => _owner.MoveFeedDown(Id));
        RemoveCommand = new RelayCommand(_ => _owner.RemoveFeed(Id));
        FetchNowCommand = new RelayCommand(_ => { _ = _owner.FetchNowAsync(Id); });

        // The single explicit Save: validate the full draft and commit it once.
        SaveCommand = new RelayCommand(_ => { Save(); });
        SaveFiltersCommand = SaveCommand;
        ResetCommand = new RelayCommand(_ => ResetDraft());

        SortingChoices =
        [
            new FilterChoice("Random", WallhavenSort.Random),
            new FilterChoice("Date added", WallhavenSort.DateAdded),
            new FilterChoice("Relevance", WallhavenSort.Relevance),
            new FilterChoice("Most viewed", WallhavenSort.Views),
            new FilterChoice("Most favourited", WallhavenSort.Favorites),
            new FilterChoice("Toplist", WallhavenSort.Toplist)
        ];
        OrderChoices =
        [
            new FilterChoice("Descending", WallhavenOrder.Descending),
            new FilterChoice("Ascending", WallhavenOrder.Ascending)
        ];
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
        ColorChoices = new ObservableCollection<ColorChoice>(
            WallhavenColors.Allowed.OrderBy(value => value, StringComparer.Ordinal).Select(value => new ColorChoice(value)));
        foreach (var choice in ColorChoices)
            choice.PropertyChanged += (_, _) => MarkDirty();

        Update(feed);
    }

    public string Id { get; }

    /// <summary>Standard shared choices plus the feed's own value; a saved cadence is never blank.</summary>
    public IReadOnlyList<int> CadenceOptions => _cadenceOptions;
    /// <summary>Alias for <see cref="CadenceOptions"/>.</summary>
    public IReadOnlyList<int> CadenceChoices => _cadenceOptions;

    /// <summary>One shared cadence list driven by Core bounds (15..10080 minutes).</summary>
    public IReadOnlyList<int> SharedCadenceChoices => WallhavenFeedStore.FeedCadenceChoices;
    public int MinimumCadenceMinutes => WallhavenFeedStore.MinimumFeedCadenceMinutes;
    public int MaximumCadenceMinutes => WallhavenFeedStore.MaximumFeedCadenceMinutes;

    public ICommand ToggleCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand SetCadenceCommand { get; }
    /// <summary>Commit a numeric cadence typed in <see cref="CustomCadenceText"/>, clamped 15..10080.</summary>
    public ICommand ApplyCustomCadenceCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand FetchNowCommand { get; }

    /// <summary>The one explicit Save: validates the whole draft and commits it once.</summary>
    public ICommand SaveCommand { get; }
    /// <summary>Alias for <see cref="SaveCommand"/>; kept for the existing Save filters button.</summary>
    public ICommand SaveFiltersCommand { get; }
    /// <summary>Discard unsaved draft edits and re-read the persisted feed.</summary>
    public ICommand ResetCommand { get; }

    /// <summary>TextBox binds with UpdateSourceTrigger=LostFocus; the setter commits the rename.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value, nameof(Name)) || _suppressCommit)
                return;
            RenameCommand.Execute(value);
        }
    }

    /// <summary>ComboBox selection commits the new cadence to the feed store.</summary>
    public int CadenceMinutes
    {
        get => _cadenceMinutes;
        set
        {
            if (!SetProperty(ref _cadenceMinutes, value, nameof(CadenceMinutes)) || _suppressCommit)
                return;
            SetCadenceCommand.Execute(value);
        }
    }

    /// <summary>Custom numeric cadence entry, in minutes. Committed by ApplyCustomCadenceCommand.</summary>
    public string CustomCadenceText
    {
        get => _customCadenceText;
        set => SetProperty(ref _customCadenceText, value, nameof(CustomCadenceText));
    }

    /// <summary>CheckBox binding commits the enable flag; binding-only is intentional.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value, nameof(IsEnabled)) || _suppressCommit)
                return;

            // TRAP: ToggleCommand flips the *current* IsEnabled. WPF has already
            // written this setter before a command could run, so calling
            // ToggleCommand.Execute(value) here reads the new value back and stores
            // its inverse; RefreshFeeds then snaps the checkbox to the old state.
            // Commit the incoming value directly, exactly once, instead.
            CommitEnabled(value);
        }
    }

    /// <summary>Fetch now follows the same global opt-in as the automatic sweep.</summary>
    public bool CanFetchNow => _owner.AutoFetchFromWallhaven;

    /// <summary>Human-readable summary of the persisted fetch/backoff/failure state.</summary>
    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value, nameof(StatusText));
    }

    /// <summary>Why the last Save was rejected; empty when the draft is valid or saved.</summary>
    public string SaveError
    {
        get => _saveError;
        private set => SetProperty(ref _saveError, value, nameof(SaveError));
    }

    /// <summary>True when the draft differs from what is persisted.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>True when the current draft would validate.</summary>
    public bool CanSave
    {
        get
        {
            try { BuildDraft(); return true; }
            catch { return false; }
        }
    }

    // ----- full WallhavenFilters editing: every field is an editable draft -----

    /// <summary>Wallhaven search expression. (Same value as <see cref="Query"/>.)</summary>
    public string FilterQuery
    {
        get => _filterQuery;
        set
        {
            if (!SetProperty(ref _filterQuery, value ?? string.Empty, nameof(FilterQuery))) return;
            OnPropertyChanged(nameof(Query));
            MarkDirty();
        }
    }

    /// <summary>Wallhaven search expression (alias for <see cref="FilterQuery"/>).</summary>
    public string Query
    {
        get => _filterQuery;
        set => FilterQuery = value;
    }

    public WallhavenCategory Categories
    {
        get => _categories;
        set
        {
            if (!SetProperty(ref _categories, value, nameof(Categories))) return;
            OnPropertyChanged(nameof(IncludeGeneral));
            OnPropertyChanged(nameof(IncludeAnime));
            OnPropertyChanged(nameof(IncludePeople));
            MarkDirty();
        }
    }

    public bool IncludeGeneral
    {
        get => _categories.HasFlag(WallhavenCategory.General);
        set => Categories = WithFlag(_categories, WallhavenCategory.General, value);
    }

    public bool IncludeAnime
    {
        get => _categories.HasFlag(WallhavenCategory.Anime);
        set => Categories = WithFlag(_categories, WallhavenCategory.Anime, value);
    }

    public bool IncludePeople
    {
        get => _categories.HasFlag(WallhavenCategory.People);
        set => Categories = WithFlag(_categories, WallhavenCategory.People, value);
    }

    public WallhavenPurity Purity
    {
        get => _purity;
        set
        {
            if (!SetProperty(ref _purity, value, nameof(Purity))) return;
            OnPropertyChanged(nameof(IncludeSafe));
            OnPropertyChanged(nameof(IncludeSketchy));
            OnPropertyChanged(nameof(IncludeExplicit));
            MarkDirty();
        }
    }

    public bool IncludeSafe
    {
        get => _purity.HasFlag(WallhavenPurity.Safe);
        set => Purity = WithFlag(_purity, WallhavenPurity.Safe, value);
    }

    public bool IncludeSketchy
    {
        get => _purity.HasFlag(WallhavenPurity.Sketchy);
        set => Purity = WithFlag(_purity, WallhavenPurity.Sketchy, value);
    }

    /// <summary>NSFW. Requires a saved Wallhaven API key; no key is stored in feed settings.</summary>
    public bool IncludeExplicit
    {
        get => _purity.HasFlag(WallhavenPurity.Explicit);
        set => Purity = WithFlag(_purity, WallhavenPurity.Explicit, value);
    }

    public WallhavenSort Sorting
    {
        get => _sorting;
        set
        {
            if (!SetProperty(ref _sorting, value, nameof(Sorting))) return;
            OnPropertyChanged(nameof(IsTopRangeEnabled));
            MarkDirty();
        }
    }

    public WallhavenOrder Order
    {
        get => _order;
        set
        {
            if (!SetProperty(ref _order, value, nameof(Order))) return;
            MarkDirty();
        }
    }

    public WallhavenTopRange TopRange
    {
        get => _topRange;
        set
        {
            if (!SetProperty(ref _topRange, value, nameof(TopRange))) return;
            MarkDirty();
        }
    }

    /// <summary>The Toplist period is only meaningful when Toplist sorting is chosen.</summary>
    public bool IsTopRangeEnabled => _sorting == WallhavenSort.Toplist;

    public string MinimumResolution
    {
        get => _minimumResolution;
        set { if (SetProperty(ref _minimumResolution, value ?? string.Empty, nameof(MinimumResolution))) MarkDirty(); }
    }

    public string ExactResolution
    {
        get => _exactResolution;
        set { if (SetProperty(ref _exactResolution, value ?? string.Empty, nameof(ExactResolution))) MarkDirty(); }
    }

    /// <summary>Aspect ratio such as 16x9, or empty for any ratio.</summary>
    public string Ratio
    {
        get => _ratio;
        set { if (SetProperty(ref _ratio, value ?? string.Empty, nameof(Ratio))) MarkDirty(); }
    }

    /// <summary>Chosen palette colours (6-digit hex, no '#').</summary>
    public IReadOnlyList<string> Colors
    {
        get => SelectedColors;
        set
        {
            var selected = new HashSet<string>(
                (value ?? Array.Empty<string>()).Select(c => c.Trim().TrimStart('#')).Where(c => c.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            _suppressCommit = true;
            try
            {
                foreach (var choice in ColorChoices)
                    choice.IsSelected = selected.Contains(choice.Hex);
            }
            finally { _suppressCommit = false; }
            OnPropertyChanged(nameof(Colors));
            MarkDirty();
        }
    }

    /// <summary>Extra comma-separated hex colours on top of the palette.</summary>
    public string CustomColors
    {
        get => _customColors;
        set { if (SetProperty(ref _customColors, value ?? string.Empty, nameof(CustomColors))) MarkDirty(); }
    }

    /// <summary>Alias for <see cref="Colors"/>.</summary>
    public IReadOnlyList<string> SelectedColors =>
        ColorChoices.Where(c => c.IsSelected).Select(c => c.Hex)
            .Concat(_customColors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim().TrimStart('#')).Where(c => c.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public ObservableCollection<ColorChoice> ColorChoices { get; }

    /// <summary>Wallhaven page this feed would fetch now, clamped into 1..LastPage.</summary>
    public int Page
    {
        get => _page;
        set { if (SetProperty(ref _page, value < 1 ? 1 : value, nameof(Page))) MarkDirty(); }
    }

    /// <summary>Wallhaven random seed for stable page walking, or empty for none.</summary>
    public string Seed
    {
        get => _seed;
        set { if (SetProperty(ref _seed, value ?? string.Empty, nameof(Seed))) MarkDirty(); }
    }

    // ----- paging state on the feed record -----

    /// <summary>1-based page this feed fetches next (Core <see cref="RotationFeed.CurrentPage"/>).</summary>
    public int CurrentPage => _currentPage;

    /// <summary>Last page Wallhaven reported, or null when unknown.</summary>
    public int? LastPage => _lastPage;

    /// <summary>How many images one fetch may take. Clamped 1..10; committed by Save.</summary>
    public int MaximumItemsPerFetch
    {
        get => _maximumItemsPerFetch;
        set
        {
            var clamped = RotationFeed.ClampMaximumItemsPerFetch(value);
            if (!SetProperty(ref _maximumItemsPerFetch, clamped, nameof(MaximumItemsPerFetch))) return;
            MarkDirty();
        }
    }

    public int MinimumMaximumItemsPerFetch => RotationFeed.MinimumMaximumItemsPerFetch;
    public int MaximumMaximumItemsPerFetch => RotationFeed.MaximumMaximumItemsPerFetch;

    public IReadOnlyList<FilterChoice> SortingChoices { get; }
    public IReadOnlyList<FilterChoice> OrderChoices { get; }
    public IReadOnlyList<FilterChoice> TopRangeChoices { get; }
    public IReadOnlyList<FilterChoice> RatioChoices { get; }

    /// <summary>Current filter draft; read-only snapshot.</summary>
    public WallhavenFilters DraftFilters
    {
        get { try { return BuildDraft(); } catch { return _lastValidFilters; } }
    }
    private WallhavenFilters _lastValidFilters = new();

    /// <summary>Reprojects fresh Core state without triggering another write.</summary>
    internal void Update(RotationFeed feed)
    {
        _suppressCommit = true;
        try
        {
            SetProperty(ref _name, feed.Name ?? string.Empty, nameof(Name));
            SetProperty(ref _cadenceMinutes, feed.CadenceMinutes, nameof(CadenceMinutes));
            SetProperty(ref _isEnabled, feed.Enabled, nameof(IsEnabled));

            _currentPage = feed.CurrentPage < 1 ? 1 : feed.CurrentPage;
            _lastPage = feed.LastPage;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(LastPage));

            RebuildCadenceOptions(_cadenceMinutes);

            if (!_isDirty)
                ReprojectDraft(feed.Filters ?? new WallhavenFilters(), feed);
        }
        finally
        {
            _suppressCommit = false;
        }

        // The master toggle lives on the owner, so a projection must refresh the
        // Fetch now button state too (the toggle calls RefreshFeeds on both edges).
        OnPropertyChanged(nameof(CanFetchNow));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        SetProperty(ref _statusText, BuildStatus(feed), nameof(StatusText));
    }

    private void ReprojectDraft(WallhavenFilters filters, RotationFeed feed)
    {
        _filterQuery = filters.Query ?? string.Empty;
        _categories = filters.Categories;
        _purity = filters.Purity;
        _sorting = filters.Sorting;
        _order = filters.Order;
        _topRange = filters.TopRange;
        _minimumResolution = filters.MinimumResolution ?? string.Empty;
        _exactResolution = filters.ExactResolution ?? string.Empty;
        _ratio = filters.Ratio ?? string.Empty;
        _customColors = string.Empty;
        _page = filters.Page < 1 ? 1 : filters.Page;
        _seed = filters.Seed ?? string.Empty;
        _maximumItemsPerFetch = RotationFeed.ClampMaximumItemsPerFetch(feed.MaximumItemsPerFetch);
        _lastValidFilters = filters;

        var selected = new HashSet<string>(
            (filters.Colors ?? Array.Empty<string>()).Select(c => c.Trim().TrimStart('#')).Where(c => c.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        foreach (var choice in ColorChoices)
            choice.IsSelected = selected.Contains(choice.Hex);

        OnPropertyChanged(nameof(FilterQuery));
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(IncludeGeneral));
        OnPropertyChanged(nameof(IncludeAnime));
        OnPropertyChanged(nameof(IncludePeople));
        OnPropertyChanged(nameof(Purity));
        OnPropertyChanged(nameof(IncludeSafe));
        OnPropertyChanged(nameof(IncludeSketchy));
        OnPropertyChanged(nameof(IncludeExplicit));
        OnPropertyChanged(nameof(Sorting));
        OnPropertyChanged(nameof(IsTopRangeEnabled));
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(TopRange));
        OnPropertyChanged(nameof(MinimumResolution));
        OnPropertyChanged(nameof(ExactResolution));
        OnPropertyChanged(nameof(Ratio));
        OnPropertyChanged(nameof(Colors));
        OnPropertyChanged(nameof(SelectedColors));
        OnPropertyChanged(nameof(CustomColors));
        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(Seed));
        OnPropertyChanged(nameof(MaximumItemsPerFetch));
    }

    /// <summary>Validates and commits the full draft in one write.</summary>
    public bool Save()
    {
        WallhavenFilters draft;
        try
        {
            draft = BuildDraft();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            SaveError = ex.Message;
            OnPropertyChanged(nameof(CanSave));
            return false;
        }

        // NSFW requires a saved Wallhaven key; no key is ever stored in feed settings.
        if (_purity.HasFlag(WallhavenPurity.Explicit) && !HasSavedWallhavenKey())
        {
            SaveError = "NSFW requires a saved Wallhaven API key. Save it in Settings; no key is stored in feed settings.";
            return false;
        }

        try
        {
            var current = WallhavenFeedStore.ListFeeds()
                .FirstOrDefault(f => string.Equals(f.Id, Id, StringComparison.OrdinalIgnoreCase));
            var baseFeed = current ?? new RotationFeed { Id = Id, Name = string.IsNullOrWhiteSpace(Name) ? Id : Name };

            var updated = baseFeed with
            {
                Name = (string.IsNullOrWhiteSpace(Name) ? baseFeed.Name : Name.Trim()),
                CadenceMinutes = WallhavenFeedStore.ClampFeedCadence(CadenceMinutes),
                Filters = draft,
                MaximumItemsPerFetch = RotationFeed.ClampMaximumItemsPerFetch(MaximumItemsPerFetch)
            };
            updated.Validate();
            WallhavenFeedStore.UpsertFeed(updated);

            _isDirty = false;
            SaveError = string.Empty;
            _lastValidFilters = draft;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanSave));

            // Prefer the owner's authoritative refresh; self-project so the row is
            // correct even if the owner has not re-read yet.
            Update(updated);
            RequestOwnerRefresh();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SaveError = $"Could not save this feed: {ex.Message}";
            return false;
        }
    }

    private void ResetDraft()
    {
        _isDirty = false;
        SaveError = string.Empty;
        var feed = WallhavenFeedStore.ListFeeds()
            .FirstOrDefault(f => string.Equals(f.Id, Id, StringComparison.OrdinalIgnoreCase));
        if (feed is not null)
        {
            Update(feed);
            return;
        }
        _suppressCommit = true;
        try { ReprojectDraft(_lastValidFilters, new RotationFeed { Id = Id, Name = Name, Filters = _lastValidFilters }); }
        finally { _suppressCommit = false; }
    }

    /// <summary>Builds a validated <see cref="WallhavenFilters"/> from the draft.</summary>
    public WallhavenFilters BuildDraft()
    {
        var colors = SelectedColors;
        var filters = new WallhavenFilters
        {
            Query = _filterQuery?.Trim() ?? string.Empty,
            Categories = _categories,
            Purity = _purity,
            Sorting = _sorting,
            Order = _order,
            TopRange = _topRange,
            MinimumResolution = string.IsNullOrWhiteSpace(_minimumResolution) ? null : _minimumResolution.Trim(),
            ExactResolution = string.IsNullOrWhiteSpace(_exactResolution) ? null : _exactResolution.Trim(),
            Ratio = string.IsNullOrWhiteSpace(_ratio) ? null : _ratio.Trim(),
            Colors = colors,
            Page = _page < 1 ? 1 : _page,
            Seed = string.IsNullOrWhiteSpace(_seed) ? null : _seed.Trim()
        };
        filters.Validate();
        return filters;
    }

    private void ApplyCustomCadence()
    {
        var raw = (_customCadenceText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out var minutes))
        {
            SaveError = $"Cadence must be a whole number of minutes between {WallhavenFeedStore.MinimumFeedCadenceMinutes} and {WallhavenFeedStore.MaximumFeedCadenceMinutes}.";
            return;
        }

        var clamped = WallhavenFeedStore.ClampFeedCadence(minutes);
        _customCadenceText = clamped.ToString();
        OnPropertyChanged(nameof(CustomCadenceText));
        SaveError = string.Empty;
        _suppressCommit = true;
        try { SetProperty(ref _cadenceMinutes, clamped, nameof(CadenceMinutes)); }
        finally { _suppressCommit = false; }
        SetCadenceCommand.Execute(clamped);
    }

    private void RebuildCadenceOptions(int current)
    {
        var shared = WallhavenFeedStore.BuildCadenceChoices(current);
        if (_cadenceOptions.Count == shared.Count && _cadenceOptions.SequenceEqual(shared))
            return;
        _cadenceOptions.Clear();
        foreach (var value in shared)
            _cadenceOptions.Add(value);
    }

    private static bool HasSavedWallhavenKey()
    {
        try { return !string.IsNullOrWhiteSpace(ApiKeyStore.Load()); }
        catch { return false; }
    }

    private void RequestOwnerRefresh()
    {
        try { _owner.RefreshFeeds(); }
        catch { /* the row is already self-projected; owner refresh is best effort */ }
    }

    private void MarkDirty()
    {
        if (_suppressCommit) return;
        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(DraftFilters));
    }

    private void CommitEnabled(bool enabled) => _owner.SetFeedEnabled(Id, enabled);

    private static WallhavenCategory WithFlag(WallhavenCategory current, WallhavenCategory flag, bool on) =>
        on ? current | flag : current & ~flag;

    private static WallhavenPurity WithFlag(WallhavenPurity current, WallhavenPurity flag, bool on) =>
        on ? current | flag : current & ~flag;

    private static string BuildStatus(RotationFeed feed)
    {
        if (!feed.Enabled)
            return "Off. Turn on this feed and the master switch to fetch automatically.";

        if (feed.BackoffUntilUtc is { } until && until > DateTimeOffset.UtcNow)
            return $"Rate-limited by Wallhaven; retrying about {until.ToLocalTime():HH:mm}.";

        var parts = new List<string>
        {
            feed.LastFetchedUtc is { } fetched
                ? $"Last fetched {fetched.ToLocalTime():g}"
                : "Not fetched yet"
        };

        var next = feed.LastFetchedUtc is { } last
            ? last + TimeSpan.FromMinutes(feed.CadenceMinutes)
            : DateTimeOffset.MinValue;
        parts.Add(next > DateTimeOffset.UtcNow
            ? $"next about {next.ToLocalTime():HH:mm}"
            : "due now");

        if (feed.ConsecutiveFailures > 0)
            parts.Add($"{feed.ConsecutiveFailures} consecutive failure(s)");
        if (!string.IsNullOrWhiteSpace(feed.LastAppliedId))
            parts.Add($"last applied #{feed.LastAppliedId}");
        if (feed.LastPage is { } lastPage)
            parts.Add($"page {feed.CurrentPage} of {lastPage}");
        else
            parts.Add($"page {feed.CurrentPage}");
        parts.Add($"{feed.MaximumItemsPerFetch} item(s)/fetch");

        return string.Join(" · ", parts) + ".";
    }
}
