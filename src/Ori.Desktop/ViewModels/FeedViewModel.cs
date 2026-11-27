using System.Collections.ObjectModel;
using System.Windows.Input;
using Ori.Core.Models;
using Ori.Desktop.Mvvm;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// One persisted Wallhaven rotation feed. The row is a projection of the Core feed
/// record: an edit is committed through <see cref="AutomationViewModel"/> to the feed
/// store, which pushes the fresh state back through <see cref="Update"/>. That way the
/// page never keeps a second, drifting copy of name/cadence/enabled/filter state.
/// </summary>
public sealed class FeedViewModel : ViewModelBase
{
    // Product spec: a feed cadence is 15 minutes to 7 days. Legacy values below the
    // floor are still shown (EnsureCadenceOption) but the next edit clamps them up.
    private static readonly int[] StandardCadences = { 15, 30, 60, 120, 240, 360, 720, 1440, 10080 };

    private readonly AutomationViewModel _owner;
    private readonly ObservableCollection<int> _cadenceOptions = new();
    private bool _suppressCommit;
    private string _name = string.Empty;
    private int _cadenceMinutes = 60;
    private bool _isEnabled;
    private string _filterQuery = string.Empty;
    private string _savedFilterQuery = string.Empty;
    private bool _filterQueryLoaded;
    private string _statusText = string.Empty;

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
        MoveUpCommand = new RelayCommand(_ => _owner.MoveFeedUp(Id));
        MoveDownCommand = new RelayCommand(_ => _owner.MoveFeedDown(Id));
        RemoveCommand = new RelayCommand(_ => _owner.RemoveFeed(Id));
        FetchNowCommand = new RelayCommand(_ => { _ = _owner.FetchNowAsync(Id); });
        SaveFiltersCommand = new RelayCommand(_ => _owner.UpdateFeedFilters(Id, FilterQuery));

        Update(feed);
    }

    public string Id { get; }

    /// <summary>Standard choices plus the feed's own value, so a saved cadence is never blank.</summary>
    public IReadOnlyList<int> CadenceOptions => _cadenceOptions;

    public ICommand ToggleCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand SetCadenceCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand FetchNowCommand { get; }
    public ICommand SaveFiltersCommand { get; }

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

    /// <summary>
    /// Editable search expression for this feed. Kept as a draft so typing cannot
    /// write to the store; Save filters validates and commits it via the owner.
    /// </summary>
    public string FilterQuery
    {
        get => _filterQuery;
        set => SetProperty(ref _filterQuery, value, nameof(FilterQuery));
    }

    /// <summary>Fetch now follows the same global opt-in as the automatic sweep.</summary>
    public bool CanFetchNow => _owner.AutoFetchFromWallhaven;

    /// <summary>Human-readable summary of the persisted fetch/backoff/failure state.</summary>
    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value, nameof(StatusText));
    }

    /// <summary>Reprojects fresh Core state without triggering another write.</summary>
    internal void Update(RotationFeed feed)
    {
        _suppressCommit = true;
        try
        {
            SetProperty(ref _name, feed.Name ?? string.Empty, nameof(Name));
            SetProperty(ref _cadenceMinutes, feed.CadenceMinutes, nameof(CadenceMinutes));
            SetProperty(ref _isEnabled, feed.Enabled, nameof(IsEnabled));

            // A feed sweep refreshes this row every few seconds. Reproject the query
            // only when there is no unsaved draft, or typing would be wiped mid-edit.
            var savedQuery = feed.Filters?.Query ?? string.Empty;
            if (!_filterQueryLoaded || string.Equals(_filterQuery, _savedFilterQuery, StringComparison.Ordinal))
                SetProperty(ref _filterQuery, savedQuery, nameof(FilterQuery));
            _savedFilterQuery = savedQuery;
            _filterQueryLoaded = true;

            SetProperty(ref _statusText, BuildStatus(feed), nameof(StatusText));
            EnsureCadenceOption(feed.CadenceMinutes);
        }
        finally
        {
            _suppressCommit = false;
        }

        // The master toggle lives on the owner, so a projection must refresh the
        // Fetch now button state too (the toggle calls RefreshFeeds on both edges).
        OnPropertyChanged(nameof(CanFetchNow));
    }

    private void CommitEnabled(bool enabled) => _owner.SetFeedEnabled(Id, enabled);

    private void EnsureCadenceOption(int minutes)
    {
        if (_cadenceOptions.Count == 0)
        {
            foreach (var standard in StandardCadences)
                _cadenceOptions.Add(standard);
        }

        if (_cadenceOptions.Contains(minutes))
            return;

        var insertAt = 0;
        while (insertAt < _cadenceOptions.Count && _cadenceOptions[insertAt] < minutes)
            insertAt++;
        _cadenceOptions.Insert(insertAt, minutes);
    }

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

        return string.Join(" · ", parts) + ".";
    }
}
