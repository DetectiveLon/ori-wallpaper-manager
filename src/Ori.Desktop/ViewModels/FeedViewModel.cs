using System.Collections.ObjectModel;
using System.Windows.Input;
using Ori.Core.Models;
using Ori.Desktop.Mvvm;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// One persisted Wallhaven rotation feed. The row is a projection of the Core feed
/// record: an edit is committed through <see cref="AutomationViewModel"/> to the feed
/// store, which pushes the fresh state back through <see cref="Update"/>. That way the
/// page never keeps a second, drifting copy of name/cadence/enabled state.
/// </summary>
public sealed class FeedViewModel : ViewModelBase
{
    private static readonly int[] StandardCadences = { 5, 10, 15, 30, 60, 120, 240, 360, 720, 1440, 10080 };

    private readonly AutomationViewModel _owner;
    private readonly ObservableCollection<int> _cadenceOptions = new();
    private bool _suppressCommit;
    private string _name = string.Empty;
    private int _cadenceMinutes = 60;
    private bool _isEnabled;
    private string _statusText = string.Empty;

    public FeedViewModel(RotationFeed feed, AutomationViewModel owner)
    {
        _owner = owner;
        Id = feed.Id;

        // Every control delegates to the owner; Core persistence and the dispatcher
        // refresh both live there, so this row stays a thin binding surface.
        ToggleCommand = new RelayCommand(_ => _owner.SetFeedEnabled(Id, !IsEnabled));
        RenameCommand = new RelayCommand(parameter => _owner.RenameFeed(Id, parameter as string ?? Name));
        SetCadenceCommand = new RelayCommand(parameter =>
            _owner.SetFeedCadence(Id, parameter is int minutes ? minutes : CadenceMinutes));
        MoveUpCommand = new RelayCommand(_ => _owner.MoveFeedUp(Id));
        MoveDownCommand = new RelayCommand(_ => _owner.MoveFeedDown(Id));
        RemoveCommand = new RelayCommand(_ => _owner.RemoveFeed(Id));
        FetchNowCommand = new RelayCommand(_ => { _ = _owner.FetchNowAsync(Id); });

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
            ToggleCommand.Execute(value);
        }
    }

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
            SetProperty(ref _statusText, BuildStatus(feed), nameof(StatusText));
            EnsureCadenceOption(feed.CadenceMinutes);
        }
        finally
        {
            _suppressCommit = false;
        }
    }

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
