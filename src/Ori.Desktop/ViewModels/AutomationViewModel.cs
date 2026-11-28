using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Ori.Core.Models;
using Ori.Core.Services;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// Binding surface over one <see cref="RotationSession"/> per connected display.
/// Every timer, gate and restore rule that used to live in ad-hoc per-card fields is
/// now session-owned; this view model only projects sessions to XAML, issues
/// commands, persists schedules and runs the serialized Wallhaven fetch/apply flow.
/// </summary>
public sealed class AutomationViewModel : ViewModelBase
{
    internal static readonly IReadOnlyDictionary<string, TimeSpan> IntervalsByName =
        new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            ["1 minute"] = TimeSpan.FromMinutes(1),
            ["5 minutes"] = TimeSpan.FromMinutes(5),
            ["10 minutes"] = TimeSpan.FromMinutes(10),
            ["15 minutes"] = TimeSpan.FromMinutes(15),
            ["30 minutes"] = TimeSpan.FromMinutes(30),
            ["1 hour"] = TimeSpan.FromHours(1),
            ["2 hours"] = TimeSpan.FromHours(2),
            ["6 hours"] = TimeSpan.FromHours(6),
            ["12 hours"] = TimeSpan.FromHours(12),
            ["1 day"] = TimeSpan.FromDays(1),
            ["2 days"] = TimeSpan.FromDays(2),
            ["3 days"] = TimeSpan.FromDays(3),
            ["7 days"] = TimeSpan.FromDays(7)
        };

    internal static readonly IReadOnlyList<string> IntervalNames = IntervalsByName.Keys.ToArray();

    /// <summary>Smallest custom interval, in whole minutes.</summary>
    public const int MinimumIntervalMinutes = ScheduleStore.MinimumIntervalMinutes;
    /// <summary>Largest custom interval, in whole minutes (7 days).</summary>
    public const int MaximumIntervalMinutes = ScheduleStore.MaximumIntervalMinutes;

    /// <summary>Prefix tagging library entries that came from one Wallhaven feed.</summary>
    public const string FeedTagPrefix = "feed:";

    internal const string ReadyStatus = "Ready. Start this display's schedule when you are.";
    private const string EmptyLibraryStatus =
        "Save wallpapers from Discover or enable a Wallhaven feed to use Start and Rotate now.";
    private static readonly TimeSpan FeedSweepInterval = TimeSpan.FromSeconds(15);

    // Bounded so a stuck COM call cannot prevent MainWindow from closing.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly WallhavenClient FeedClient = new(WallhavenClient.CreateHttpClient());
    private static readonly WallpaperImageCache FeedImages = new();

    private readonly WallpaperLibraryStore _library = new();
    private readonly ScheduleStore _schedules = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _feedTimer;

    // The UI mutates _sessions while rotation/feed callbacks read it from the thread
    // pool. Every dictionary access goes through this lock; callers snapshot to an
    // array so no enumeration happens while another thread changes the dictionary.
    private readonly object _sessionsGate = new();
    private readonly Dictionary<string, RotationSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DisplayScheduleViewModel> _cards =
        new(StringComparer.OrdinalIgnoreCase);

    // Displays in the middle of being unplugged. A snapshot taken by another session
    // must not resurrect their saved schedule.
    private readonly HashSet<string> _sessionsBeingRemoved = new(StringComparer.OrdinalIgnoreCase);

    // Last set of states written successfully. A transient LoadAsync failure must
    // never turn into an empty save that deletes schedules for unplugged monitors.
    private IReadOnlyDictionary<string, DisplayScheduleState> _lastKnownStates =
        new Dictionary<string, DisplayScheduleState>(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _feedCts = new();
    private int _feedFetchInFlight;
    private int _feedSweepPending;
    private bool _pauseWhenFullscreen = true;
    private bool _autoFetchFromWallhaven;
    private bool _shutdownRequested;
    private bool _libraryHasImages;
    private bool _deferredSchedulesExist;
    private int _deferredStartInFlight;
    private bool _initialized;
    private string _globalStatus = "Preparing…";
    private string _libraryStatus = string.Empty;
    private string _displayStatus = string.Empty;
    private string _feedStatus = string.Empty;
    private string? _lastTrayTooltip;

    private RotationSession? _selectedSession;
    private bool _fetchApplyToAll;
    private IReadOnlyList<string> _availableCollections = [];

    public AutomationViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateAllSessions();
        _feedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FeedSweepInterval };
        _feedTimer.Tick += OnFeedTimerTick;

        RefreshDisplaysCommand = new RelayCommand(_ => { _ = RefreshAvailabilityAsync(); });
        AddFeedFromDiscoverCommand = new RelayCommand(_ => NavigateToDiscover());

        _pauseWhenFullscreen = AutomationPreferencesStore.LoadPauseWhenFullscreen();

        try { _autoFetchFromWallhaven = WallhavenFeedStore.IsAutoFetchEnabled(); }
        catch { _autoFetchFromWallhaven = false; }

        // Hotplug: adopt saved schedules by fingerprint, add new displays, stop removed
        // ones without losing saved schedules. Never throw from the handler.
        DisplayDetection.DisplaysChanged += OnDisplaysChanged;
    }

    // ----- hotplug -----

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (_shutdownRequested || _dispatcher.HasShutdownStarted)
            return;
        try
        {
            _ = TryInvokeOnDispatcherAsync(async () =>
            {
                if (_shutdownRequested) return;
                await RefreshDisplaysAsync(restoreNewDisplays: true).ConfigureAwait(false);
            });
        }
        catch
        {
            // A hotplug notification must never throw.
        }
    }

    // ----- dispatcher helpers -----

    private async Task TryInvokeOnDispatcherAsync(Action action)
    {
        if (_shutdownRequested || _dispatcher.HasShutdownStarted) return;
        try
        {
            if (_dispatcher.CheckAccess()) action();
            else await _dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        }
        catch { /* shutdown can kill the dispatcher between check and marshal */ }
    }

    private async Task TryInvokeOnDispatcherAsync(Func<Task> action)
    {
        if (_shutdownRequested || _dispatcher.HasShutdownStarted) return;
        try
        {
            if (_dispatcher.CheckAccess()) await action().ConfigureAwait(false);
            else
            {
                var operation = _dispatcher.InvokeAsync(action);
                await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch { /* best-effort */ }
    }

    // ----- status -----

    private void SetLibraryStatus(string message) =>
        _ = TryInvokeOnDispatcherAsync(() => { _libraryStatus = message; UpdateGlobalStatus(); });

    internal void ReportLoadFailure(Exception exception) =>
        SetLibraryStatus($"Could not load automation settings: {exception.Message}");

    private void SetDisplayStatus(string message) { _displayStatus = message; UpdateGlobalStatus(); }

    private void SetFeedStatus(string message) =>
        _ = TryInvokeOnDispatcherAsync(() => { _feedStatus = message; UpdateGlobalStatus(); });

    private void UpdateGlobalStatus()
    {
        var parts = new[] { _libraryStatus, _displayStatus, _feedStatus }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        GlobalStatus = string.Join(' ', parts);
    }

    // ----- bindable collections -----

    /// <summary>One row per connected display, rendered by the AutomationView DataTemplate.</summary>
    public ObservableCollection<DisplayScheduleViewModel> Schedules { get; } = new();

    /// <summary>Projection of the persisted Wallhaven feeds, in store order.</summary>
    public ObservableCollection<FeedViewModel> Feeds { get; } = new();

    /// <summary>Collections currently in the library, for per-display Collection sources.</summary>
    public IReadOnlyList<string> AvailableCollections => _availableCollections;

    public string GlobalStatus
    {
        get => _globalStatus;
        private set => SetProperty(ref _globalStatus, value, nameof(GlobalStatus));
    }

    /// <summary>App-wide master for full-screen pausing. Per-display opt-outs combine with this.</summary>
    public bool PauseWhenFullscreen
    {
        get => Volatile.Read(ref _pauseWhenFullscreen);
        set
        {
            if (!SetProperty(ref _pauseWhenFullscreen, value, nameof(PauseWhenFullscreen))) return;
            AutomationPreferencesStore.SavePauseWhenFullscreen(value);
            UpdateAllSessions();
        }
    }

    /// <summary>App-wide opt-in for hands-free Wallhaven fetching. Each feed has its own flag too.</summary>
    public bool AutoFetchFromWallhaven
    {
        get => Volatile.Read(ref _autoFetchFromWallhaven);
        set
        {
            if (!SetProperty(ref _autoFetchFromWallhaven, value, nameof(AutoFetchFromWallhaven))) return;

            try { WallhavenFeedStore.SetAutoFetchEnabled(value); } catch { /* in-memory still applies */ }

            if (value)
            {
                EnsureFeedCts();
                RefreshFeeds();
                SetFeedStatus("Auto-fetch is on. Enabled feeds fetch on their own cadence.");
                _ = TryStartDeferredSchedulesAsync();
                _ = FetchDueFeedSafelyAsync();
            }
            else
            {
                CancelFeedFetch();
                RefreshFeeds();
                SetFeedStatus(string.Empty);
                UpdateAllSessions();
            }
        }
    }

    public ICommand RefreshDisplaysCommand { get; }
    public ICommand AddFeedFromDiscoverCommand { get; }

    // ----- Fetch now targeting -----

    /// <summary>The display Fetch now targets. Null falls back to first running / primary.</summary>
    public DisplayScheduleViewModel? SelectedDisplay
    {
        get => FindCard(_selectedSession);
        set
        {
            if (SetProperty(ref _selectedSession, value?.Session, nameof(SelectedDisplay)))
                OnPropertyChanged(nameof(SelectedDisplay));
        }
    }

    /// <summary>Explicit "apply to all running" option for Fetch now.</summary>
    public bool FetchApplyToAll
    {
        get => _fetchApplyToAll;
        set => SetProperty(ref _fetchApplyToAll, value, nameof(FetchApplyToAll));
    }

    private static void NavigateToDiscover()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel shell)
            shell.Navigate("discover");
    }

    // ----- lifecycle -----

    public async Task InitializeAsync()
    {
        if (_shutdownRequested) return;

        var firstLoad = !_initialized;

        RefreshFeeds();
        await RefreshLibraryStatusAsync();
        if (_shutdownRequested) return;

        await RefreshDisplaysAsync(restoreNewDisplays: false);
        if (_shutdownRequested) return;

        if (!firstLoad)
        {
            await TryStartDeferredSchedulesAsync();
            return;
        }

        await RestoreSchedulesAsync();
        if (_shutdownRequested) return;
        if (!_shutdownRequested && !_uiTimer.IsEnabled) _uiTimer.Start();
        if (!_shutdownRequested && !_feedTimer.IsEnabled) _feedTimer.Start();
        _initialized = true;
    }

    private async Task TryStartDeferredSchedulesAsync()
    {
        if (_shutdownRequested || Interlocked.Exchange(ref _deferredStartInFlight, 1) != 0) return;
        try
        {
            if (!_deferredSchedulesExist || (!_libraryHasImages && !HasEnabledFeed())) return;
            await RestoreSchedulesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { SetLibraryStatus($"Could not start the waiting schedules: {ex.Message}"); }
        finally { Volatile.Write(ref _deferredStartInFlight, 0); }
    }

    private async Task RefreshAvailabilityAsync()
    {
        try
        {
            await RefreshLibraryStatusAsync();
            await RefreshDisplaysAsync();
        }
        catch (Exception ex) { SetLibraryStatus($"Could not refresh automation state: {ex.Message}"); }
    }

    public void RequestStop()
    {
        _shutdownRequested = true;
        _uiTimer.Stop();
        _feedTimer.Stop();
        CancelFeedFetch();

        DisplayDetection.DisplaysChanged -= OnDisplaysChanged;

        foreach (var session in SnapshotSessions())
        {
            session.UserPaused = false;
            session.CancelLoops();
        }
    }

    public async Task StopAsync()
    {
        RequestStop();

        var tasks = SnapshotSessions().Select(session => StopSessionAsync(session)).ToArray();
        if (tasks.Length == 0) return;

        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all, Task.Delay(ShutdownTimeout));
    }

    // ----- sessions snapshot -----

    private RotationSession[] SnapshotSessions()
    {
        lock (_sessionsGate) return _sessions.Values.ToArray();
    }

    private DisplayScheduleViewModel? FindCard(RotationSession? session)
    {
        if (session is null) return null;
        lock (_sessionsGate)
        {
            _cards.TryGetValue(session.Monitor.Id, out var card);
            return card;
        }
    }

    internal RotationSession? FindSession(RotationSession session) => session;

    // ----- persistence -----

    private async Task PersistSessionAsync(RotationSession session, bool? shouldRun = null)
    {
        if (_shutdownRequested) return;

        try
        {
            await _persistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                DisplayScheduleState[] states;
                lock (_sessionsGate)
                {
                    states = _sessions.Values
                        .Where(current => !_sessionsBeingRemoved.Contains(current.Monitor.Id)
                                          || (ReferenceEquals(current, session) && shouldRun.HasValue))
                        .Select(current => new DisplayScheduleState(
                            current.Monitor.Id,
                            current.IntervalName,
                            ReferenceEquals(current, session) && shouldRun.HasValue
                                ? shouldRun.Value
                                : current.ActiveSchedule,
                            current.UserPaused,
                            current.NextRunUtc,
                            current.LastAppliedId,
                            current.PauseWhenFullscreen,
                            current.Fingerprint,
                            current.PreviousAppliedPath,
                            current.SourceKind,
                            current.SourceId)).ToArray();
                }

                await SaveMergedStatesCoreAsync(states).ConfigureAwait(false);
            }
            finally { _persistGate.Release(); }
        }
        catch { /* a failed persistence write must never stop the active schedule */ }
    }

    private async Task SaveMergedStatesAsync(
        IEnumerable<DisplayScheduleState> overrides,
        IEnumerable<string>? removeIds = null)
    {
        try
        {
            await _persistGate.WaitAsync();
            try { await SaveMergedStatesCoreAsync(overrides, removeIds).ConfigureAwait(false); }
            finally { _persistGate.Release(); }
        }
        catch { /* best-effort */ }
    }

    private async Task SaveMergedStatesCoreAsync(
        IEnumerable<DisplayScheduleState> overrides,
        IEnumerable<string>? removeIds = null)
    {
        IReadOnlyDictionary<string, DisplayScheduleState> saved;
        try { saved = await _schedules.LoadAsync().ConfigureAwait(false); }
        catch { saved = _lastKnownStates; }
        if (saved.Count == 0) saved = _lastKnownStates;

        var merged = new Dictionary<string, DisplayScheduleState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in saved) merged[pair.Key] = pair.Value;

        if (removeIds is not null)
            foreach (var id in removeIds) merged.Remove(id);

        foreach (var state in overrides) merged[state.MonitorId] = state;

        await _schedules.SaveAsync(merged.Values).ConfigureAwait(false);
        _lastKnownStates = new Dictionary<string, DisplayScheduleState>(merged, StringComparer.OrdinalIgnoreCase);
    }

    // ----- schedule restore / hotplug adoption -----

    private async Task RestoreSchedulesAsync(IReadOnlyCollection<RotationSession>? onlySessions = null)
    {
        if (_shutdownRequested) return;
        if (onlySessions is null) _deferredSchedulesExist = false;

        IReadOnlyDictionary<string, DisplayScheduleState> saved;
        try { saved = await _schedules.LoadAsync(); }
        catch { return; }

        var scope = onlySessions is null ? null : new HashSet<RotationSession>(onlySessions);
        var byId = new Dictionary<string, DisplayScheduleState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in saved) byId[pair.Key] = pair.Value;

        var resolved = new Dictionary<RotationSession, DisplayScheduleState>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adoptedFrom = new List<string>();
        var adopted = false;
        var identityDirty = false;

        var sessions = SnapshotSessions();

        // Pass one: a saved entry whose id matches a live display belongs to that display.
        foreach (var session in sessions)
        {
            var fingerprint = session.Fingerprint;
            if (!byId.TryGetValue(session.Monitor.Id, out var direct)) continue;

            var savedFingerprint = direct.MonitorFingerprint;
            if (!string.IsNullOrWhiteSpace(savedFingerprint)
                && !string.Equals(savedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                var sameDevice = string.Equals(
                    FingerprintDeviceName(savedFingerprint), session.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase);
                var anotherPanelOwnsIt = sessions.Any(other => !ReferenceEquals(other, session)
                    && string.Equals(other.Fingerprint, savedFingerprint, StringComparison.OrdinalIgnoreCase));
                if (!sameDevice || anotherPanelOwnsIt) continue;
            }

            claimed.Add(session.Monitor.Id);
            if (!string.Equals(savedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                direct = direct with { MonitorFingerprint = fingerprint };
                byId[session.Monitor.Id] = direct;
                identityDirty = true;
            }
            resolved[session] = direct;
        }

        // Pass two: adopt saved entries no live display is already using.
        foreach (var session in sessions)
        {
            if (resolved.ContainsKey(session)) continue;

            var fingerprint = session.Fingerprint;
            string? matchId = null;
            DisplayScheduleState? matchState = null;
            foreach (var pair in byId)
            {
                if (claimed.Contains(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value.MonitorFingerprint)
                    || !string.Equals(pair.Value.MonitorFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    continue;
                matchId = pair.Key;
                matchState = pair.Value;
                break;
            }

            if (matchId is null && !string.IsNullOrWhiteSpace(session.Monitor.DeviceName))
            {
                var sameDevice = byId
                    .Where(pair => !claimed.Contains(pair.Key))
                    .Where(pair => string.Equals(
                        FingerprintDeviceName(pair.Value.MonitorFingerprint),
                        session.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sameDevice.Length == 1)
                {
                    matchId = sameDevice[0].Key;
                    matchState = sameDevice[0].Value;
                }
            }

            if (matchId is null || matchState is null) continue;

            var adoptedState = matchState with
            {
                MonitorId = session.Monitor.Id,
                MonitorFingerprint = fingerprint
            };
            byId.Remove(matchId);
            byId[session.Monitor.Id] = adoptedState;
            adoptedFrom.Add(matchId);
            claimed.Add(session.Monitor.Id);
            resolved[session] = adoptedState;
            adopted = true;
        }

        var restored = new List<(RotationSession Session, DisplayScheduleState State)>();
        foreach (var session in sessions)
            if (resolved.TryGetValue(session, out var state) && (scope is null || scope.Contains(session)))
                restored.Add((session, state));

        var desired = new List<DisplayScheduleState>(restored.Count);
        foreach (var (session, state) in restored)
        {
            // Restore custom + legacy interval names alike.
            if (ScheduleStore.TryParseIntervalName(state.IntervalName, out var minutes))
                session.IntervalName = ScheduleStore.FormatIntervalName(minutes);
            else if (IntervalsByName.ContainsKey(state.IntervalName))
                session.IntervalName = state.IntervalName;

            session.LastAppliedId = state.LastAppliedId;
            session.PreviousAppliedPath = state.PreviousAppliedPath;
            session.PauseWhenFullscreen = state.PauseWhenFullscreen;
            session.SourceKind = Enum.IsDefined(state.SourceKind) ? state.SourceKind : RotationSourceKind.Library;
            session.SourceId = string.IsNullOrWhiteSpace(state.SourceId) ? null : state.SourceId.Trim();

            if (!session.IsRunning)
            {
                session.UserPaused = state.UserPaused;
                session.NextRunUtc = state.NextRunUtc;
                session.ActiveSchedule = state.ShouldRun;
            }

            desired.Add(state with
            {
                MonitorId = session.Monitor.Id,
                MonitorFingerprint = session.Fingerprint,
                IntervalName = session.IntervalName,
                PauseWhenFullscreen = session.PauseWhenFullscreen,
                LastAppliedId = session.LastAppliedId,
                UserPaused = session.UserPaused,
                NextRunUtc = session.NextRunUtc,
                ShouldRun = session.ActiveSchedule,
                PreviousAppliedPath = session.PreviousAppliedPath,
                SourceKind = session.SourceKind,
                SourceId = session.SourceId
            });
        }

        if (adopted || identityDirty || desired.Count > 0)
            await SaveMergedStatesAsync(desired, adoptedFrom);

        // Refresh cards (interval/source changes) after in-memory restore.
        foreach (var (session, _) in restored) UpdateSessionUi(session);

        foreach (var (session, state) in restored)
        {
            if (!state.ShouldRun) { UpdateSessionUi(session); continue; }

            if (!_libraryHasImages && !HasEnabledFeed())
            {
                _deferredSchedulesExist = true;
                session.StatusMessage = "Saved schedule is waiting for wallpapers in the local library or an enabled feed.";
                UpdateSessionUi(session);
                continue;
            }

            await StartRotationAsync(session, state.NextRunUtc, restoring: true);
        }
    }

    private static string? FingerprintDeviceName(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var separator = fingerprint.IndexOf('|');
        var name = separator < 0 ? fingerprint : fingerprint[..separator];
        return name.Trim();
    }

    private async Task RefreshLibraryStatusAsync()
    {
        if (_shutdownRequested) return;

        int count;
        string message;
        IReadOnlyList<WallpaperLibraryEntry> entries;
        try
        {
            entries = await _library.GetAllAsync();
            count = entries.Count;
            message = count == 0
                ? "Your local library is empty. Save wallpapers from Discover or enable a Wallhaven feed."
                : $"{count} wallpaper(s) are saved locally and available for offline rotation.";
        }
        catch (Exception ex)
        {
            entries = [];
            count = 0;
            message = $"Could not read the local library: {ex.Message}";
        }

        var collections = entries
            .Select(e => e.Collection)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var startDeferred = false;
        await TryInvokeOnDispatcherAsync(() =>
        {
            if (_shutdownRequested) return;
            startDeferred = !_libraryHasImages && count > 0;
            _libraryHasImages = count > 0;
            _libraryStatus = message;
            _availableCollections = collections;
            OnPropertyChanged(nameof(AvailableCollections));
            UpdateGlobalStatus();
            UpdateAllSessions();
        }).ConfigureAwait(false);

        if (startDeferred)
        {
            Func<Task> restoreWaiting = () => TryStartDeferredSchedulesAsync();
            await TryInvokeOnDispatcherAsync(restoreWaiting).ConfigureAwait(false);
        }
    }

    // ----- hotplug display refresh -----

    internal async Task RefreshDisplaysAsync(bool restoreNewDisplays = true)
    {
        if (_shutdownRequested) return;

        try
        {
            var detection = DisplayDetection.Read();
            if (!detection.Succeeded)
            {
                SetDisplayStatus($"Could not refresh displays: {detection.Error}");
                return;
            }

            var monitors = detection.Monitors;
            var connectedIds = monitors.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Removed displays: cancel in-memory, keep saved schedule on disk.
            foreach (var removed in SnapshotSessions().Where(s => !connectedIds.Contains(s.Monitor.Id)))
            {
                lock (_sessionsGate) _sessionsBeingRemoved.Add(removed.Monitor.Id);
                try
                {
                    await StopSessionAsync(removed, keepSavedSchedule: true);
                    DisplayScheduleViewModel? card;
                    lock (_sessionsGate)
                    {
                        _cards.Remove(removed.Monitor.Id, out card);
                        _sessions.Remove(removed.Monitor.Id);
                    }
                    if (card is not null) Schedules.Remove(card);
                }
                finally
                {
                    lock (_sessionsGate) _sessionsBeingRemoved.Remove(removed.Monitor.Id);
                }
            }

            var added = new List<RotationSession>();
            foreach (var monitor in monitors)
            {
                RotationSession? existing;
                lock (_sessionsGate) _sessions.TryGetValue(monitor.Id, out existing);
                if (existing is not null)
                {
                    existing.Monitor = monitor;
                    existing.RefreshFingerprint();
                    UpdateSessionUi(existing);
                    continue;
                }

                var session = new RotationSession(monitor);
                session.StatusMessage = ReadyStatus;
                lock (_sessionsGate) _sessions.Add(monitor.Id, session);
                var card = new DisplayScheduleViewModel(session, this);
                lock (_sessionsGate) _cards.Add(monitor.Id, card);
                Schedules.Add(card);
                UpdateSessionUi(session);
                added.Add(session);
            }

            SetDisplayStatus(monitors.Count == 0
                ? "Windows did not report any connected displays."
                : $"{monitors.Count} display(s) detected. Start a schedule on each display you want Ori to manage.");

            if (restoreNewDisplays && added.Count > 0)
                await RestoreSchedulesAsync(added);
        }
        catch (Exception ex)
        {
            SetDisplayStatus($"Could not refresh displays: {ex.Message}");
        }
    }

    // ----- interval editing -----

    internal void OnIntervalChanged(DisplayScheduleViewModel card, string intervalName)
    {
        var session = card.Session;
        if (!ScheduleStore.TryParseIntervalName(intervalName, out var minutes))
        {
            UpdateSessionUi(session); // revert UI; nothing persisted for garbage
            return;
        }

        var canonical = ScheduleStore.FormatIntervalName(minutes);
        session.IntervalName = canonical;
        card.EnsureInterval(canonical);
        UpdateSessionUi(session);
        _ = PersistSessionAsync(session);
    }

    internal bool TryCommitCustomInterval(DisplayScheduleViewModel card, string? rawMinutes, out string message)
    {
        message = string.Empty;
        var trimmed = rawMinutes?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !int.TryParse(trimmed, out var minutes))
        {
            message = $"Enter a whole number of minutes between {MinimumIntervalMinutes} and {MaximumIntervalMinutes}.";
            return false;
        }
        if (minutes < MinimumIntervalMinutes || minutes > MaximumIntervalMinutes)
        {
            message = $"Custom interval must be {MinimumIntervalMinutes}..{MaximumIntervalMinutes} whole minutes.";
            return false;
        }

        var canonical = ScheduleStore.FormatIntervalName(minutes);
        card.Session.IntervalName = canonical;
        card.EnsureInterval(canonical);
        card.CustomMinutesInput = minutes.ToString();
        UpdateSessionUi(card.Session);
        _ = PersistSessionAsync(card.Session);
        message = $"Custom interval set to {canonical}.";
        return true;
    }

    // ----- per-display source binding -----

    internal void OnSourceChanged(DisplayScheduleViewModel card)
    {
        UpdateSessionUi(card.Session);
        _ = PersistSessionAsync(card.Session);
        card.RaiseSourceOptions();
    }

    // ----- rotation -----

    internal async Task StartRotationAsync(RotationSession session, DateTimeOffset? resumeAt = null, bool restoring = false)
    {
        if (_shutdownRequested || session.IsStarting || session.IsRunning) return;

        if (!ScheduleStore.TryParseIntervalName(session.IntervalName, out var minutes))
        {
            session.StatusMessage = "Choose a valid rotation interval (1..10080 whole minutes).";
            UpdateSessionUi(session);
            return;
        }
        var interval = TimeSpan.FromMinutes(minutes);

        session.IsStarting = true;
        session.State = RotationState.Preparing;
        UpdateSessionUi(session);

        try
        {
            var available = await GatherAvailableAsync(session, CancellationToken.None);
            if (_shutdownRequested) return;

            if (available.Count == 0 && HasEnabledFeed())
            {
                try { await FetchDueFeedAsync(_feedCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { SetFeedStatus($"Could not fetch a rotation feed: {ex.Message}"); }

                if (_shutdownRequested) return;
                available = await GatherAvailableAsync(session, CancellationToken.None);
            }

            if (available.Count == 0)
            {
                session.StatusMessage = HasEnabledFeed()
                    ? "No images match this display's source yet. Use Fetch now or wait for the next sweep."
                    : "No available images for this display's source. Save wallpapers from Discover or pick another source.";
                session.State = RotationState.Idle;
                UpdateSessionUi(session);
                return;
            }

            session.LoopCancellation = new CancellationTokenSource();
            session.ActiveSchedule = true;
            if (!restoring) session.UserPaused = false;
            session.NextRunUtc = resumeAt ?? DateTimeOffset.UtcNow + interval;
            session.State = RotationState.Running;
            session.StatusMessage = restoring
                ? "Schedule restored. Rotation continues from the saved deadline."
                : $"{available.Count} wallpaper(s) available for this display's source. This schedule is running.";

            var token = session.LoopCancellation.Token;
            var task = session.Loop.RunAsync(
                interval,
                shouldPause: () => ShouldPause(session),
                rotateAsync: cancellationToken => RotateFromLibraryAsync(
                    session, cancellationToken, respectPause: true,
                    expectedManualRotationGeneration: Volatile.Read(ref session.ManualRotationGeneration)),
                nextRunChanged: async nextRun =>
                {
                    await TryInvokeOnDispatcherAsync(() =>
                    {
                        session.NextRunUtc = nextRun;
                        session.State = session.UserPaused || session.FullscreenPaused ? RotationState.Paused : RotationState.Running;
                        UpdateSessionUi(session);
                    }).ConfigureAwait(false);
                    await PersistSessionAsync(session);
                },
                failed: async exception =>
                {
                    await TryInvokeOnDispatcherAsync(() =>
                    {
                        session.StatusMessage = $"Rotation failed: {exception.Message}";
                        UpdateSessionUi(session);
                    }).ConfigureAwait(false);
                },
                cancellationToken: token,
                lastCompletedAt: resumeAt.HasValue ? resumeAt.Value - interval : null);

            session.LoopTask = task;
            session.LoopObserverTask = ObserveLoopAsync(session, task);
            await PersistSessionAsync(session, shouldRun: true);
        }
        catch (Exception ex)
        {
            session.StatusMessage = $"Could not start rotation: {ex.Message}";
            await StopSessionAsync(session);
            session.StatusMessage = $"Could not start rotation: {ex.Message}";
            UpdateSessionUi(session);
        }
        finally
        {
            session.IsStarting = false;
            UpdateSessionUi(session);
        }
    }

    private async Task<IReadOnlyList<WallpaperLibraryEntry>> GatherAvailableAsync(RotationSession session, CancellationToken ct)
    {
        IReadOnlyList<WallpaperLibraryEntry> entries;
        try { entries = await _library.GetAllAsync(ct); }
        catch { return []; }
        return SelectCandidates(session, entries);
    }

    /// <summary>Filters library entries to this display's source; keeps only files on disk.</summary>
    public static IReadOnlyList<WallpaperLibraryEntry> SelectCandidates(
        RotationSession session, IReadOnlyList<WallpaperLibraryEntry> entries)
    {
        var available = entries.Where(e => !string.IsNullOrWhiteSpace(e.LocalPath) && File.Exists(e.LocalPath)).ToList();
        return session.SourceKind switch
        {
            RotationSourceKind.Collection when !string.IsNullOrWhiteSpace(session.SourceId) =>
                available.Where(e => string.Equals(e.Collection, session.SourceId, StringComparison.OrdinalIgnoreCase)).ToList(),
            RotationSourceKind.Feed =>
                available.Where(e => HasFeedTag(e.Tags, FeedTagPrefix + (session.SourceId ?? string.Empty))).ToList(),
            _ => available
        };
    }

    /// <summary>True when a comma-separated tag list contains <paramref name="tag"/>.</summary>
    public static bool HasFeedTag(string? tags, string tag)
    {
        if (string.IsNullOrWhiteSpace(tags) || string.IsNullOrWhiteSpace(tag)) return false;
        foreach (var piece in tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(piece, tag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private async Task ObserveLoopAsync(RotationSession session, Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { session.StatusMessage = $"Rotation stopped: {ex.Message}"; }
        finally
        {
            if (Application.Current is not null)
                await TryInvokeOnDispatcherAsync(() =>
                {
                    if (ReferenceEquals(session.LoopTask, task)) FinishLoop(session, task);
                }).ConfigureAwait(false);
        }
    }

    private void FinishLoop(RotationSession session, Task task)
    {
        if (!ReferenceEquals(session.LoopTask, task)) return;
        session.LoopTask = null;
        session.LoopCancellation?.Dispose();
        session.LoopCancellation = null;
        session.NextRunUtc = null;
        session.UserPaused = false;
        session.State = RotationState.Idle;
        UpdateSessionUi(session);
    }

    private bool ShouldPause(RotationSession session) =>
        session.UserPaused
        || (Volatile.Read(ref _pauseWhenFullscreen)
            && session.PauseWhenFullscreen
            && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor));

    internal void OnSessionFullscreenPauseChanged(RotationSession session)
    {
        UpdateSessionUi(session);
        _ = PersistSessionAsync(session);
    }

    internal void TogglePause(RotationSession session)
    {
        if (!session.IsRunning) return;
        session.UserPaused = !session.UserPaused;
        session.State = session.UserPaused ? RotationState.Paused : RotationState.Running;
        session.StatusMessage = session.UserPaused ? "Paused manually." : "Manual pause released.";
        UpdateSessionUi(session);
        _ = PersistSessionAsync(session);
    }

    internal async Task RotateNowAsync(RotationSession session, bool respectPause = false)
    {
        if (_shutdownRequested || session.IsStarting || session.ManualRotationRunning) return;

        session.ManualRotationRunning = true;
        session.State = RotationState.Applying;
        var loopToken = session.LoopCancellation?.Token ?? CancellationToken.None;
        var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        session.ManualCancellation = manualCancellation;
        UpdateSessionUi(session);

        var operation = RotateFromLibraryAsync(session, manualCancellation.Token, respectPause: respectPause);
        session.ManualTask = operation;

        try
        {
            await operation;
            Interlocked.Increment(ref session.ManualRotationGeneration);
            if (session.IsRunning) session.Loop.ResetNextRun();
        }
        catch (OperationCanceledException) when (manualCancellation.IsCancellationRequested)
        {
            session.StatusMessage = "Manual rotation was cancelled.";
        }
        catch (Exception ex) { session.StatusMessage = $"Could not rotate this display: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(session.ManualCancellation, manualCancellation)) session.ManualCancellation = null;
            session.ManualTask = null;
            manualCancellation.Dispose();
            session.ManualRotationRunning = false;
            session.State = session.IsRunning ? (session.UserPaused || session.FullscreenPaused ? RotationState.Paused : RotationState.Running) : RotationState.Idle;
            UpdateSessionUi(session);
        }
    }

    internal async Task PreviousAsync(RotationSession session)
    {
        if (_shutdownRequested || session.IsStarting || session.PreviousRotationRunning || session.ManualRotationRunning)
            return;

        var previousPath = session.PreviousAppliedPath;
        if (string.IsNullOrWhiteSpace(previousPath) || !File.Exists(previousPath))
        {
            session.StatusMessage = string.IsNullOrWhiteSpace(previousPath)
                ? "No previous wallpaper on this display yet. Rotate once, then use Previous."
                : "The previous wallpaper file is no longer available.";
            UpdateSessionUi(session);
            return;
        }

        session.PreviousRotationRunning = true;
        session.State = RotationState.Applying;
        var loopToken = session.LoopCancellation?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        session.ManualCancellation = linked;
        UpdateSessionUi(session);

        try
        {
            var entries = await _library.GetAllAsync(linked.Token).ConfigureAwait(false);
            var matchedId = entries.FirstOrDefault(entry => PathsEqual(entry.LocalPath, previousPath))?.Id;

            await _rotationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var monitor = session.Monitor;
                    WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, previousPath);
                    session.PreviousAppliedPath = session.LastAppliedPath;
                    session.LastAppliedPath = previousPath;
                    session.LastAppliedId = matchedId;
                    session.Monitor = monitor with { CurrentWallpaper = previousPath };
                    session.StatusMessage = $"Restored the previous wallpaper on {monitor.DeviceName}.";
                    UpdateSessionUi(session);
                });
            }
            finally { _rotationGate.Release(); }

            if (session.IsRunning) session.Loop.ResetNextRun();
            await PersistSessionAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { session.StatusMessage = "Returning to the previous wallpaper was cancelled."; }
        catch (Exception ex) { session.StatusMessage = $"Could not restore the previous wallpaper: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(session.ManualCancellation, linked)) session.ManualCancellation = null;
            session.PreviousRotationRunning = false;
            session.State = session.IsRunning ? (session.UserPaused || session.FullscreenPaused ? RotationState.Paused : RotationState.Running) : RotationState.Idle;
            UpdateSessionUi(session);
        }
    }

    private async Task RotateFromLibraryAsync(
        RotationSession session, CancellationToken cancellationToken, bool respectPause, int? expectedManualRotationGeneration = null)
    {
        if (expectedManualRotationGeneration is int expectedBeforeLoad
            && expectedBeforeLoad != Volatile.Read(ref session.ManualRotationGeneration))
            return;

        var entries = await _library.GetAllAsync(cancellationToken);
        var available = SelectCandidates(session, entries);
        if (available.Count == 0)
            throw new InvalidOperationException("No available images match this display's source.");

        var monitorAtSelection = session.Monitor;
        var lastId = session.LastAppliedId;
        if (lastId is null && !string.IsNullOrWhiteSpace(monitorAtSelection.CurrentWallpaper))
            lastId = available.FirstOrDefault(e => PathsEqual(e.LocalPath, monitorAtSelection.CurrentWallpaper))?.Id;

        var pending = session.PendingFeedEntryId is { } pendingId
            ? available.FirstOrDefault(e => string.Equals(e.Id, pendingId, StringComparison.OrdinalIgnoreCase))
            : null;

        WallpaperLibraryEntry selected;
        if (pending is not null) selected = pending;
        else
        {
            var candidates = available.Count > 1
                ? available.Where(e => !string.Equals(e.Id, lastId, StringComparison.OrdinalIgnoreCase)).ToList()
                : available;
            if (candidates.Count == 0) candidates = available;
            selected = candidates[Random.Shared.Next(candidates.Count)];
        }

        var applied = false;
        await _rotationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedManualRotationGeneration is int expectedBeforeApply
                && expectedBeforeApply != Volatile.Read(ref session.ManualRotationGeneration))
                return;

            await _dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (expectedManualRotationGeneration is int expectedBeforeDispatch
                    && expectedBeforeDispatch != Volatile.Read(ref session.ManualRotationGeneration))
                {
                    session.StatusMessage = "Scheduled change skipped because a manual rotation just completed.";
                    UpdateSessionUi(session);
                    return;
                }

                if (respectPause && ShouldPause(session))
                {
                    session.StatusMessage = "Scheduled change deferred because this display is paused.";
                    UpdateSessionUi(session);
                    return;
                }

                if (!File.Exists(selected.LocalPath))
                    throw new FileNotFoundException("The selected library image is missing.", selected.LocalPath);

                if (!ReferenceEquals(session.Monitor, monitorAtSelection))
                {
                    session.StatusMessage = "Scheduled change skipped: this display changed during rotation.";
                    UpdateSessionUi(session);
                    return;
                }

                var monitor = session.Monitor;
                WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, selected.LocalPath);

                var outgoing = session.LastAppliedPath;
                if (string.IsNullOrWhiteSpace(outgoing)) outgoing = monitorAtSelection.CurrentWallpaper;
                if (!string.IsNullOrWhiteSpace(outgoing) && !PathsEqual(outgoing, selected.LocalPath))
                    session.PreviousAppliedPath = outgoing;
                session.LastAppliedPath = selected.LocalPath;
                session.LastAppliedId = selected.Id;
                session.PendingFeedEntryId = null;
                session.Monitor = monitor with { CurrentWallpaper = selected.LocalPath };
                session.StatusMessage = $"Applied {selected.Title} to {monitor.DeviceName}.";
                UpdateSessionUi(session);
                applied = true;
            });
        }
        finally { _rotationGate.Release(); }

        if (applied) await PersistSessionAsync(session);
    }

    // ----- serialized Wallhaven fetch + apply (429 backoff preserved in Core) -----

    private CancellationToken EnsureFeedCts()
    {
        if (_feedCts is { IsCancellationRequested: false }) return _feedCts.Token;
        try { _feedCts.Dispose(); } catch { /* already disposed */ }
        _feedCts = new CancellationTokenSource();
        return _feedCts.Token;
    }

    private void CancelFeedFetch()
    {
        try
        {
            if (!_feedCts.IsCancellationRequested)
            {
                _feedCts.Cancel();
                Volatile.Write(ref _feedSweepPending, 1);
            }
        }
        catch { /* already cancelled */ }
    }

    private async void OnFeedTimerTick(object? sender, EventArgs e)
    {
        try
        {
            if (_shutdownRequested || Volatile.Read(ref _feedFetchInFlight) != 0 || !AutoFetchFromWallhaven) return;
            await FetchDueFeedSafelyAsync();
        }
        catch (Exception ex) { SetFeedStatus($"Feed check failed: {ex.Message}"); }
    }

    private async Task FetchDueFeedSafelyAsync()
    {
        try { await FetchDueFeedAsync(_feedCts.Token); }
        catch (OperationCanceledException) { Volatile.Write(ref _feedFetchInFlight, 0); }
        catch (Exception ex) { SetFeedStatus($"Feed check failed: {ex.Message}"); }
    }

    /// <summary>Due-feed sweep; serialized app-wide. Queues images, never resets countdowns.</summary>
    internal async Task FetchDueFeedAsync(CancellationToken cancellationToken)
    {
        var acquired = Interlocked.Exchange(ref _feedFetchInFlight, 1) == 0;
        if (_shutdownRequested)
        {
            if (acquired) Volatile.Write(ref _feedFetchInFlight, 0);
            return;
        }
        if (!acquired) { Volatile.Write(ref _feedSweepPending, 1); return; }

        try
        {
            Interlocked.Exchange(ref _feedSweepPending, 0);
            if (!AutoFetchFromWallhaven) return;

            RotationFeed? feed;
            try { feed = WallhavenFeedStore.GetNextDueFeed(DateTimeOffset.UtcNow); }
            catch (Exception ex) { SetFeedStatus($"Could not check rotation feeds: {ex.Message}"); return; }

            if (feed is null || !feed.Enabled) return;

            await FetchFeedAsync(feed.Id, cancellationToken, FetchTarget.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _feedFetchInFlight, 0);
            if (Interlocked.Exchange(ref _feedSweepPending, 0) == 1
                && !_shutdownRequested && AutoFetchFromWallhaven)
                _ = FetchDueFeedSafelyAsync();
        }
    }

    /// <summary>Fetch one named feed now, ignoring its cadence. Global Wallhaven opt-in gates this.</summary>
    internal Task FetchNowAsync(string feedId) =>
        FetchNowAsync(feedId, _fetchApplyToAll);

    /// <summary>
    /// Fetch now. By default targets the selected display only; an explicit
    /// apply-to-all option applies to every running display. Prior wallpaper is
    /// preserved on failure.
    /// </summary>
    internal async Task FetchNowAsync(string feedId, bool applyToAll)
    {
        if (!AutoFetchFromWallhaven)
        {
            SetFeedStatus("Turn on automatic Wallhaven fetching before using Fetch now.");
            return;
        }

        if (_shutdownRequested || Interlocked.Exchange(ref _feedFetchInFlight, 1) != 0)
        {
            SetFeedStatus("A Wallhaven fetch is already running.");
            return;
        }

        var token = EnsureFeedCts();
        try
        {
            var targets = ResolveFetchTargets(applyToAll, explicitTarget: null);
            await FetchFeedAsync(feedId, token, new FetchTarget(targets, applyToAll)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Volatile.Write(ref _feedFetchInFlight, 0);
            SetFeedStatus("Wallhaven fetch cancelled.");
        }
        catch (Exception ex) { SetFeedStatus($"Could not fetch this feed: {ex.Message}"); }
        finally { Volatile.Write(ref _feedFetchInFlight, 0); }
    }

    /// <summary>Per-display Fetch now. Targets this display unless apply-to-all is requested.</summary>
    internal async Task FetchNowForDisplayAsync(RotationSession session)
    {
        var feedId = session.SourceKind == RotationSourceKind.Feed && !string.IsNullOrWhiteSpace(session.SourceId)
            ? session.SourceId!
            : FirstEnabledFeedId();
        if (feedId is null)
        {
            SetFeedStatus("No enabled rotation feed to fetch for this display.");
            return;
        }

        // Route through the same serialized path but pin this display.
        if (!AutoFetchFromWallhaven)
        {
            SetFeedStatus("Turn on automatic Wallhaven fetching before using Fetch now.");
            return;
        }
        if (_shutdownRequested || Interlocked.Exchange(ref _feedFetchInFlight, 1) != 0)
        {
            SetFeedStatus("A Wallhaven fetch is already running.");
            return;
        }

        var token = EnsureFeedCts();
        try
        {
            var targets = _fetchApplyToAll
                ? SnapshotSessions().Where(s => s.IsRunning).ToArray()
                : [session];
            await FetchFeedAsync(feedId, token, new FetchTarget(targets, _fetchApplyToAll)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Volatile.Write(ref _feedFetchInFlight, 0);
            SetFeedStatus("Wallhaven fetch cancelled.");
        }
        catch (Exception ex) { SetFeedStatus($"Could not fetch this feed: {ex.Message}"); }
        finally { Volatile.Write(ref _feedFetchInFlight, 0); }
    }

    private IReadOnlyList<RotationSession> ResolveFetchTargets(bool applyToAll, RotationSession? explicitTarget)
    {
        var sessions = SnapshotSessions();
        if (applyToAll)
            return sessions.Where(s => s.IsRunning).ToArray();

        if (explicitTarget is not null) return [explicitTarget];

        var selected = _selectedSession;
        if (selected is not null && sessions.Contains(selected)) return [selected];

        var running = sessions.Where(s => s.IsRunning).ToArray();
        if (running.Length > 0) return [running[0]];

        var primary = sessions.FirstOrDefault(s => s.Monitor.IsPrimary);
        if (primary is not null) return [primary];

        return sessions.Length > 0 ? [sessions[0]] : [];
    }

    private string? FirstEnabledFeedId()
    {
        try
        {
            return WallhavenFeedStore.ListFeeds().FirstOrDefault(f => f.Enabled)?.Id;
        }
        catch { return null; }
    }

    /// <summary>Where fetched images go. <see cref="FetchTarget.None"/> = queue only, no apply.</summary>
    private readonly record struct FetchTarget(IReadOnlyList<RotationSession> Targets, bool ApplyToAll)
    {
        public static FetchTarget None => new([], false);
    }

    /// <summary>
    /// Downloads up to <see cref="RotationFeed.MaximumItemsPerFetch"/> new wallpapers for
    /// one feed using <see cref="WallhavenFeedStore.GetFetchRequest"/>/<see cref="WallhavenFeedStore.RecordFetchPage"/>,
    /// tags them <c>feed:&lt;id&gt;</c>, records Core fetch/backoff state, and applies to
    /// the resolved targets. App-wide fetching is serialized by the caller. Prior
    /// wallpaper is restored on apply failure. Network failures never stop a loop.
    /// </summary>
    private async Task FetchFeedAsync(string feedId, CancellationToken cancellationToken, FetchTarget target)
    {
        var feed = FindFeed(feedId);
        if (feed is null) { SetFeedStatus("That rotation feed no longer exists."); return; }

        var feedName = string.IsNullOrWhiteSpace(feed.Name) ? "Untitled feed" : feed.Name;
        string? appliedId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetFeedStatus($"Fetching new wallpapers for '{feedName}'…");

            string apiKey;
            try { apiKey = ApiKeyStore.Load(); } catch { apiKey = string.Empty; }

            // Page/seed walking via Core; NOT hard-coded page 1. The explicit seed
            // falls back to the feed's own filter seed, so a user-configured seed is
            // never overwritten by a null page-walk seed.
            var now = DateTimeOffset.UtcNow;
            var fetchRequest = WallhavenFeedStore.GetFetchRequest(feed, now);
            var page = await FeedClient.SearchAsync(
                feed.Filters, fetchRequest.Page, fetchRequest.Seed,
                apiKey: apiKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var maxItems = Math.Clamp(feed.MaximumItemsPerFetch, 1, 10);

            try
            {
                var entries = await _library.GetAllAsync(cancellationToken).ConfigureAwait(false);
                var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.SourceId)) knownIds.Add(entry.SourceId);
                    if (!string.IsNullOrWhiteSpace(entry.ImageUrl)) knownUrls.Add(entry.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(entry.PageUrl)) knownUrls.Add(entry.PageUrl);
                    knownIds.Add(entry.Id);
                }

                var feedTag = FeedTagPrefix + feedId;
                var addedIds = new List<string>();
                var reachedEndOfPage = true;
                foreach (var wallpaper in page.Items)
                {
                    if (addedIds.Count >= maxItems)
                    {
                        // Unseen items are still on this page. Stay here so the next
                        // fetch picks them up instead of skipping them entirely.
                        reachedEndOfPage = false;
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var imageUrl = wallpaper.ImageUrl.ToString();
                    var pageUrl = wallpaper.PageUrl.ToString();
                    if (knownIds.Contains(wallpaper.Id) || knownUrls.Contains(imageUrl) || knownUrls.Contains(pageUrl))
                    {
                        // Still tag an already-present image for this feed source.
                        var existing = entries.FirstOrDefault(e =>
                            string.Equals(e.Id, wallpaper.Id, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(e.SourceId, wallpaper.Id, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null && !HasFeedTag(existing.Tags, feedTag))
                        {
                            try { await _library.SetTagsAsync(existing.Id, MergeTags(existing.Tags, feedTag), cancellationToken); }
                            catch (Exception ex) when (IsLocalIoFailure(ex)) { /* local */ }
                        }
                        continue;
                    }

                    var path = await FeedImages.SaveToLibraryAsync(wallpaper, cancellationToken).ConfigureAwait(false);
                    var entry = new WallpaperLibraryEntry
                    {
                        Id = wallpaper.Id,
                        Title = $"Wallhaven #{wallpaper.Id}",
                        PageUrl = pageUrl,
                        ImageUrl = imageUrl,
                        LocalPath = path,
                        Category = wallpaper.Category,
                        Purity = wallpaper.Purity,
                        Width = wallpaper.Width,
                        Height = wallpaper.Height,
                        Ratio = wallpaper.Ratio,
                        SavedAtUtc = DateTimeOffset.UtcNow,
                        Collection = "Unsorted",
                        SourceId = wallpaper.Id,
                        SourceQuery = feed.Filters.Query,
                        Colors = string.Join(", ", wallpaper.Colors),
                        Tags = feedTag
                    };

                    await _library.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                    knownIds.Add(wallpaper.Id);
                    knownUrls.Add(imageUrl);
                    knownUrls.Add(pageUrl);
                    addedIds.Add(entry.Id);
                }

                string message;
                if (addedIds.Count == 0)
                {
                    message = $"'{feedName}' found no new wallpapers.";
                }
                else
                {
                    appliedId = await ApplyFetchedWallpapersAsync(
                        addedIds[0], feedName, target, cancellationToken).ConfigureAwait(false);
                    message = appliedId is not null
                        ? $"Added {addedIds.Count} new wallpaper(s) from '{feedName}' and applied one."
                        : $"Added {addedIds.Count} new wallpaper(s) from '{feedName}'. The first one is pending for the next rotation.";
                }

                // Advance only after the images are safely on disk. A local IO failure
                // above leaves CurrentPage unchanged so the same page is retried.
                if (reachedEndOfPage)
                {
                    try
                    {
                        WallhavenFeedStore.RecordFetchPage(
                            feedId, fetchRequest.Page, page.LastPage, page.Seed ?? fetchRequest.Seed);
                    }
                    catch { /* paging state must not hide a successful download */ }
                }

                try { WallhavenFeedStore.RecordFetchResult(feedId, true, DateTimeOffset.UtcNow, appliedId); }
                catch { /* a failed state write must not hide success */ }
                await RefreshLibraryStatusAsync().ConfigureAwait(false);
                RefreshFeeds();
                SetFeedStatus(message);
                NotifyTray("Ori · rotation feeds", message);
            }
            catch (Exception ex) when (IsLocalIoFailure(ex))
            {
                var localMessage = $"Could not save wallpapers from '{feedName}': {ex.Message}";
                SetFeedStatus(localMessage);
                NotifyTray("Ori · rotation feeds", localMessage, error: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try { WallhavenFeedStore.RecordFetchResult(feedId, false, DateTimeOffset.UtcNow); } catch { /* best-effort */ }
            var updated = FindFeed(feedId);
            var failures = updated?.ConsecutiveFailures ?? 1;
            var backoff = updated?.BackoffUntilUtc;
            var message = backoff is { } until && until > DateTimeOffset.UtcNow
                ? $"'{feedName}' hit a Wallhaven limit; retrying about {until.ToLocalTime():HH:mm} (failure {failures})."
                : $"Could not fetch '{feedName}': {ex.Message}" + (failures > 1 ? $" ({failures} failures in a row.)" : string.Empty);
            SetFeedStatus(message);
            NotifyTray("Ori · rotation feeds", message, error: true);
            RefreshFeeds();
        }
    }

    /// <summary>Comma-separated tag lists are merged case-insensitively, de-duplicated.</summary>
    public static string MergeTags(string? existing, string addTag)
    {
        var set = (existing ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(string.IsNullOrWhiteSpace(addTag) ? [] : [addTag.Trim()])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join(", ", set);
    }

    private static bool IsLocalIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;

    /// <summary>
    /// Applies a freshly downloaded image. By default only the selected/resolved
    /// target(s); explicit apply-to-all hits every running display. On any apply
    /// failure the prior wallpaper is restored. Never throws past a per-display guard.
    /// </summary>
    private async Task<string?> ApplyFetchedWallpapersAsync(
        string entryId, string feedName, FetchTarget target, CancellationToken cancellationToken)
    {
        // Scheduled sweep (no explicit target): queue for the next rotation, don't apply.
        if (target.Targets.Count == 0)
        {
            await QueueFetchedWallpapersAsync(entryId, feedName).ConfigureAwait(false);
            return null;
        }

        var targets = target.Targets.ToArray();
        var anyApplied = false;
        foreach (var session in targets)
        {
            var ok = await ApplyFetchedToSessionAsync(session, entryId, feedName, cancellationToken).ConfigureAwait(false);
            anyApplied |= ok;
        }

        if (target.ApplyToAll)
        {
            foreach (var session in targets.Where(s => s.PendingFeedEntryId is not null))
                session.StatusMessage = $"Fetched from '{feedName}'. The new image will be applied on the next change.";
        }

        return anyApplied ? entryId : null;
    }

    private async Task<bool> ApplyFetchedToSessionAsync(
        RotationSession session, string entryId, string feedName, CancellationToken cancellationToken)
    {
        var entry = (await _library.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.Id, entryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !File.Exists(entry.LocalPath)) return false;

        var monitorAtSelection = session.Monitor;
        // Snapshot the currently-applied wallpaper so we can restore it on failure.
        var priorWallpaper = !string.IsNullOrWhiteSpace(session.LastAppliedPath) && File.Exists(session.LastAppliedPath)
            ? session.LastAppliedPath
            : (!string.IsNullOrWhiteSpace(monitorAtSelection.CurrentWallpaper) && File.Exists(monitorAtSelection.CurrentWallpaper)
                ? monitorAtSelection.CurrentWallpaper
                : session.PreviousAppliedPath);
        var priorValid = !string.IsNullOrWhiteSpace(priorWallpaper) && File.Exists(priorWallpaper!);

        if (ShouldPause(session))
        {
            session.PendingFeedEntryId = entryId;
            session.StatusMessage = $"Fetched from '{feedName}'. The new image will be applied when this display is no longer paused.";
            UpdateSessionUi(session);
            return false;
        }

        await _rotationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var applied = false;
            await _dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldPause(session) || !ReferenceEquals(monitorAtSelection, session.Monitor))
                {
                    session.PendingFeedEntryId = entryId;
                    session.StatusMessage = $"Fetched from '{feedName}'. The new image will be applied on the next change.";
                    UpdateSessionUi(session);
                    return;
                }
                if (!File.Exists(entry.LocalPath)) return;

                var monitor = session.Monitor;
                try
                {
                    WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, entry.LocalPath);
                }
                catch
                {
                    // Preserve the prior wallpaper on failure.
                    if (priorValid)
                    {
                        try { WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, priorWallpaper!); } catch { /* nothing more we can do */ }
                    }
                    session.StatusMessage = $"Could not apply '{feedName}'. Prior wallpaper preserved.";
                    UpdateSessionUi(session);
                    throw;
                }

                var outgoing = session.LastAppliedPath;
                if (string.IsNullOrWhiteSpace(outgoing)) outgoing = monitorAtSelection.CurrentWallpaper;
                if (!string.IsNullOrWhiteSpace(outgoing) && !PathsEqual(outgoing, entry.LocalPath))
                    session.PreviousAppliedPath = outgoing;
                session.LastAppliedPath = entry.LocalPath;
                session.LastAppliedId = entry.Id;
                session.Monitor = monitor with { CurrentWallpaper = entry.LocalPath };
                session.PendingFeedEntryId = null;
                session.StatusMessage = $"Applied {entry.Title} to {monitor.DeviceName}.";
                UpdateSessionUi(session);
                applied = true;
            });
            if (applied) await PersistSessionAsync(session).ConfigureAwait(false);
            return applied;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Restore prior wallpaper if a partial apply occurred.
            if (priorValid)
            {
                try { _dispatcher.Invoke(() => WindowsDesktopWallpaper.ApplyWallpaper(session.Monitor.Id, priorWallpaper!)); }
                catch { /* best-effort restore */ }
            }
            session.StatusMessage = $"Could not apply '{feedName}': {ex.Message}. Prior wallpaper preserved.";
            UpdateSessionUi(session);
            return false;
        }
        finally { _rotationGate.Release(); }
    }

    private async Task QueueFetchedWallpapersAsync(string entryId, string feedName)
    {
        var running = SnapshotSessions().Where(s => s.IsRunning).ToArray();
        if (running.Length == 0 || _dispatcher.HasShutdownStarted) return;

        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var session in running) session.PendingFeedEntryId = entryId;
            foreach (var session in running.Where(s =>
                         !ShouldPause(s) && !s.IsStarting && !s.ManualRotationRunning && !s.PreviousRotationRunning))
            {
                session.StatusMessage = $"Fetched from '{feedName}'. The new image will be applied on the next scheduled change.";
                UpdateSessionUi(session);
            }
        });
    }

    // ----- feed projection + mutation (called by FeedViewModel) -----

    private bool HasEnabledFeed() => AutoFetchFromWallhaven && Feeds.Any(feed => feed.IsEnabled);

    internal void RefreshFeeds()
    {
        void apply()
        {
            IReadOnlyList<RotationFeed> feeds;
            try { feeds = WallhavenFeedStore.ListFeeds(); }
            catch (Exception ex) { SetFeedStatus($"Could not read rotation feeds: {ex.Message}"); return; }

            var existing = Feeds.ToDictionary(feed => feed.Id, StringComparer.OrdinalIgnoreCase);
            for (var index = Feeds.Count - 1; index >= 0; index--)
                if (feeds.All(feed => !string.Equals(feed.Id, Feeds[index].Id, StringComparison.OrdinalIgnoreCase)))
                    Feeds.RemoveAt(index);

            for (var index = 0; index < feeds.Count; index++)
            {
                var feed = feeds[index];
                if (index < Feeds.Count && string.Equals(Feeds[index].Id, feed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Feeds[index].Update(feed);
                    continue;
                }
                if (existing.TryGetValue(feed.Id, out var viewModel))
                {
                    var oldIndex = Feeds.IndexOf(viewModel);
                    if (oldIndex >= 0) Feeds.RemoveAt(oldIndex);
                    Feeds.Insert(Math.Min(index, Feeds.Count), viewModel);
                    viewModel.Update(feed);
                    continue;
                }
                Feeds.Insert(Math.Min(index, Feeds.Count), new FeedViewModel(feed, this));
            }

            UpdateAllSessions();
        }

        _ = TryInvokeOnDispatcherAsync(apply);
    }

    internal void SetFeedEnabled(string id, bool enabled) => MutateFeed(id, feed => feed with { Enabled = enabled });

    internal void RenameFeed(string id, string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) { RefreshFeeds(); SetFeedStatus("A rotation feed needs a name."); return; }
        MutateFeed(id, feed => feed with { Name = trimmed });
    }

    internal void SetFeedCadence(string id, int minutes)
    {
        var clamped = Math.Clamp(minutes, 15, 7 * 24 * 60);
        MutateFeed(id, feed => feed with { CadenceMinutes = clamped });
    }

    internal void UpdateFeedFilters(string id, string query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (MutateFeed(id, feed => feed with { Filters = feed.Filters with { Query = trimmed } }))
            SetFeedStatus("Saved the feed's search filters.");
    }

    internal void MoveFeedUp(string id) => MoveFeed(id, -1);
    internal void MoveFeedDown(string id) => MoveFeed(id, 1);

    private void MoveFeed(string id, int offset)
    {
        try
        {
            var feeds = WallhavenFeedStore.ListFeeds();
            var index = FindFeedIndex(feeds, id);
            var target = index + offset;
            if (index >= 0 && target >= 0 && target < feeds.Count) WallhavenFeedStore.MoveFeed(id, target);
        }
        catch (Exception ex) { SetFeedStatus($"Could not reorder rotation feeds: {ex.Message}"); }
        RefreshFeeds();
    }

    internal void RemoveFeed(string id)
    {
        try { WallhavenFeedStore.RemoveFeed(id); }
        catch (Exception ex) { SetFeedStatus($"Could not remove the feed: {ex.Message}"); }
        RefreshFeeds();
    }

    private bool MutateFeed(string id, Func<RotationFeed, RotationFeed> mutate)
    {
        try
        {
            var feed = FindFeed(id);
            if (feed is null) return false;
            var updated = mutate(feed);
            updated.Validate();
            WallhavenFeedStore.UpsertFeed(updated);
        }
        catch (Exception ex) { SetFeedStatus($"Could not update the feed: {ex.Message}"); RefreshFeeds(); return false; }
        RefreshFeeds();
        return true;
    }

    private static RotationFeed? FindFeed(string id)
    {
        try
        {
            return WallhavenFeedStore.ListFeeds().FirstOrDefault(feed =>
                string.Equals(feed.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static int FindFeedIndex(IReadOnlyList<RotationFeed> feeds, string id)
    {
        for (var index = 0; index < feeds.Count; index++)
            if (string.Equals(feeds[index].Id, id, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    // ----- stopping -----

    internal async Task StopSessionAsync(RotationSession session, bool keepSavedSchedule = false)
    {
        var loopCancellation = session.LoopCancellation;
        var loopTask = session.LoopTask;
        var manualTask = session.ManualTask;

        session.ActiveSchedule = false;
        session.State = RotationState.Idle;
        session.ManualCancellation?.Cancel();
        loopCancellation?.Cancel();

        if (manualTask is not null)
        {
            try { await manualTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { session.StatusMessage = $"Manual rotation stopped: {ex.Message}"; }
        }

        if (loopTask is not null)
        {
            try { await loopTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { session.StatusMessage = $"Rotation stopped: {ex.Message}"; }
        }

        var observer = session.LoopObserverTask;
        if (observer is not null)
        {
            try { await observer; } catch { /* observer already reports */ }
            if (ReferenceEquals(session.LoopObserverTask, observer)) session.LoopObserverTask = null;
        }

        if (ReferenceEquals(session.LoopTask, loopTask) && loopTask is not null) FinishLoop(session, loopTask);

        if (ReferenceEquals(session.LoopCancellation, loopCancellation))
        {
            session.LoopCancellation?.Dispose();
            session.LoopCancellation = null;
        }

        // Releases any manual CTS left behind and satisfies the session's own
        // dispose contract. A restart creates fresh cancellation sources.
        session.Dispose();

        session.UserPaused = false;
        session.NextRunUtc = null;
        session.State = RotationState.Idle;
        if (!_shutdownRequested)
        {
            session.StatusMessage = "Schedule stopped.";
            UpdateSessionUi(session);
        }
        if (!keepSavedSchedule) await PersistSessionAsync(session, shouldRun: false);
    }

    // ----- UI projection -----

    private void UpdateAllSessions()
    {
        foreach (var session in SnapshotSessions()) UpdateSessionUi(session);
    }

    internal void UpdateSessionUi(RotationSession session)
    {
        var card = FindCard(session);
        var running = session.IsRunning;
        var fullscreenPauseAllowed = Volatile.Read(ref _pauseWhenFullscreen) && session.PauseWhenFullscreen;
        session.FullscreenPaused = running && fullscreenPauseAllowed && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor);

        // Recompute explicit state from session facts.
        if (!running)
        {
            session.State = session.IsStarting ? RotationState.Preparing
                : (session.ManualRotationRunning || session.PreviousRotationRunning ? RotationState.Applying : RotationState.Idle);
        }
        else if (session.UserPaused || session.FullscreenPaused)
        {
            session.State = RotationState.Paused;
        }
        else if (!session.ManualRotationRunning && !session.PreviousRotationRunning)
        {
            session.State = RotationState.Running;
        }

        var heading = session.Monitor.IsPrimary ? $"{session.Monitor.DeviceName}  ·  PRIMARY" : session.Monitor.DeviceName;
        var subheading = $"{session.Monitor.Width:N0} × {session.Monitor.Height:N0}  ·  Position {session.Monitor.Left}, {session.Monitor.Top}";

        string countdown;
        if (!running || session.NextRunUtc is null)
        {
            countdown = session.IsStarting ? "Preparing…"
                : (session.ManualRotationRunning || session.PreviousRotationRunning ? "Applying…" : "Not running");
        }
        else if (session.UserPaused || session.FullscreenPaused)
        {
            countdown = "Paused";
        }
        else
        {
            var remaining = session.NextRunUtc.Value - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var days = (int)remaining.TotalDays;
            countdown = days > 0
                ? $"{days}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        var canUseFeed = HasEnabledFeed();
        var hasSource = _libraryHasImages || canUseFeed;
        // Per-display source narrowing: a Feed-bound display needs matching tagged images.
        var sourceEmpty = !hasSource;
        var statusText = ResolveStatusText(session, running, sourceEmpty);

        var busy = running || session.IsStarting || session.ManualRotationRunning || session.PreviousRotationRunning;
        var canStart = !busy && !_shutdownRequested && hasSource;
        var canPause = running;
        var pauseButtonText = session.UserPaused ? "Resume" : "Pause";
        var canRotateNow = !session.IsStarting && !session.ManualRotationRunning && !session.PreviousRotationRunning
            && !_shutdownRequested && hasSource;
        var canUsePrevious = !session.IsStarting && !session.ManualRotationRunning && !session.PreviousRotationRunning
            && !_shutdownRequested && !string.IsNullOrWhiteSpace(session.PreviousAppliedPath) && File.Exists(session.PreviousAppliedPath);
        var canStop = busy;
        var intervalEnabled = !busy;

        card?.ApplyProjection(
            heading, subheading, session.State, countdown, statusText, pauseButtonText,
            canStart, canPause, canRotateNow, canUsePrevious, canStop, intervalEnabled);

        UpdateTrayTooltip();
    }

    private static string ResolveStatusText(RotationSession session, bool running, bool libraryEmpty)
    {
        if (session.UserPaused) return "Paused manually. Resume to restart the full interval.";
        if (session.FullscreenPaused)
            return "Paused: a full-screen foreground window is active on this display. The full interval restarts when it closes.";
        if (libraryEmpty && !running) return EmptyLibraryStatus;
        return string.IsNullOrWhiteSpace(session.StatusMessage) ? ReadyStatus : session.StatusMessage;
    }

    private void UpdateTrayTooltip()
    {
        try
        {
            var next = SnapshotSessions()
                .Where(s => s.IsRunning && s.NextRunUtc is not null && !s.UserPaused && !s.FullscreenPaused)
                .Select(s => s.NextRunUtc).Where(v => v is not null).OrderBy(v => v).FirstOrDefault();

            var text = next is { } deadline ? $"Ori — next change {deadline.ToLocalTime():HH:mm}" : "Ori — wallpaper manager";
            if (string.Equals(text, _lastTrayTooltip, StringComparison.Ordinal)) return;
            _lastTrayTooltip = text;
            App.Tray?.UpdateTooltip(text);
        }
        catch { /* tray optional */ }
    }

    // ----- helpers -----

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static void NotifyTray(string title, string message, bool error = false)
    {
        try { App.Tray?.Notify(title, message, error); } catch { /* tray optional */ }
    }

    // ----- tiny app preference store -----

    private static class AutomationPreferencesStore
    {
        private static readonly string PreferencesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ori", "automation-preferences.json");
        private static readonly object Gate = new();

        public static bool LoadPauseWhenFullscreen()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(PreferencesPath)) return true;
                    using var document = JsonDocument.Parse(File.ReadAllText(PreferencesPath));
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("pauseWhenFullscreen", out var flag))
                        return true;
                    return flag.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => true };
                }
                catch { return true; }
            }
        }

        public static void SavePauseWhenFullscreen(bool enabled)
        {
            lock (Gate)
            {
                try
                {
                    var directory = Path.GetDirectoryName(PreferencesPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    var temporary = PreferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary,
                            "{\"version\":1,\"pauseWhenFullscreen\":" + (enabled ? "true" : "false") + "}");
                        File.Move(temporary, PreferencesPath, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                catch { /* best-effort */ }
            }
        }
    }
}

/// <summary>Explicit live state of one display's rotation, independent of localized text.</summary>
public enum RotationState
{
    Idle,
    Preparing,
    Running,
    Paused,
    Applying
}

/// <summary>Thin per-card binding surface over a <see cref="RotationSession"/>.</summary>
public sealed class DisplayScheduleViewModel : ViewModelBase
{
    private readonly AutomationViewModel _owner;
    private readonly ObservableCollection<string> _intervals = new();

    private string _heading = string.Empty;
    private string _subheading = string.Empty;
    private string _countdown = "Not running";
    private string _statusText = AutomationViewModel.ReadyStatus;
    private string _pauseButtonText = "Pause";
    private RotationState _rotationState = RotationState.Idle;
    private bool _canStart;
    private bool _canPause;
    private bool _canRotateNow;
    private bool _canUsePrevious;
    private bool _canStop;
    private bool _intervalEnabled = true;
    private string _customMinutesInput = string.Empty;
    private string _customMinutesStatus = string.Empty;

    public DisplayScheduleViewModel(RotationSession session, AutomationViewModel owner)
    {
        Session = session;
        _owner = owner;
        foreach (var name in AutomationViewModel.IntervalNames) _intervals.Add(name);
        EnsureInterval(session.IntervalName);

        StartCommand = new RelayCommand(_ => { _ = _owner.StartRotationAsync(Session); });
        PauseCommand = new RelayCommand(_ => _owner.TogglePause(Session));
        PreviousCommand = new RelayCommand(_ => { _ = _owner.PreviousAsync(Session); });
        RotateNowCommand = new RelayCommand(_ => { _ = _owner.RotateNowAsync(Session); });
        StopCommand = new RelayCommand(_ => { _ = _owner.StopSessionAsync(Session); });
        CommitCustomIntervalCommand = new RelayCommand(_ =>
        {
            _owner.TryCommitCustomInterval(this, CustomMinutesInput, out var msg); CustomMinutesStatus = msg;
        });
        SelectCommand = new RelayCommand(_ => _owner.SelectedDisplay = this);
        FetchNowCommand = new RelayCommand(_ => { _ = _owner.FetchNowForDisplayAsync(Session); });
    }

    /// <summary>The engine state this card projects.</summary>
    public RotationSession Session { get; }

    public string Heading { get => _heading; private set => SetProperty(ref _heading, value, nameof(Heading)); }
    public string Subheading { get => _subheading; private set => SetProperty(ref _subheading, value, nameof(Subheading)); }
    public RotationState RotationState { get => _rotationState; private set => SetProperty(ref _rotationState, value, nameof(RotationState)); }
    public string Countdown { get => _countdown; private set => SetProperty(ref _countdown, value, nameof(Countdown)); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value, nameof(StatusText)); }
    public string PauseButtonText { get => _pauseButtonText; private set => SetProperty(ref _pauseButtonText, value, nameof(PauseButtonText)); }
    public bool CanStart { get => _canStart; private set => SetProperty(ref _canStart, value, nameof(CanStart)); }
    public bool CanPause { get => _canPause; private set => SetProperty(ref _canPause, value, nameof(CanPause)); }
    public bool CanRotateNow { get => _canRotateNow; private set => SetProperty(ref _canRotateNow, value, nameof(CanRotateNow)); }
    public bool CanUsePrevious { get => _canUsePrevious; private set => SetProperty(ref _canUsePrevious, value, nameof(CanUsePrevious)); }
    public bool CanStop { get => _canStop; private set => SetProperty(ref _canStop, value, nameof(CanStop)); }
    public bool IntervalEnabled { get => _intervalEnabled; private set => SetProperty(ref _intervalEnabled, value, nameof(IntervalEnabled)); }

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand RotateNowCommand { get; }
    public ICommand StopCommand { get; }
    /// <summary>Explicit commit for the custom-minutes input (1..10080 whole minutes).</summary>
    public ICommand CommitCustomIntervalCommand { get; }
    /// <summary>Make this display the Fetch now target.</summary>
    public ICommand SelectCommand { get; }
    /// <summary>Per-display Fetch now (targets this display; honors the global apply-to-all).</summary>
    public ICommand FetchNowCommand { get; }

    /// <summary>Interval choices: built-ins plus any custom value in use.</summary>
    public IReadOnlyList<string> Intervals => _intervals;

    /// <summary>Guard-required binding. Selecting sets the persisted interval.</summary>
    public string SelectedInterval
    {
        get => Session.IntervalName;
        set
        {
            var requested = value ?? string.Empty;
            if (string.Equals(requested, Session.IntervalName, StringComparison.Ordinal)) return;
            // Owner validates, canonicalizes, persists and notifies PropertyChanged.
            _owner.OnIntervalChanged(this, requested);
        }
    }

    /// <summary>Per-display full-screen pause opt-out (combined with the app master).</summary>
    public bool PauseWhenFullscreen
    {
        get => Session.PauseWhenFullscreen;
        set
        {
            if (Session.PauseWhenFullscreen == value) return;
            Session.PauseWhenFullscreen = value;
            OnPropertyChanged(nameof(PauseWhenFullscreen));
            _owner.OnSessionFullscreenPauseChanged(Session);
        }
    }

    // ----- per-display source binding -----

    /// <summary>Rotation source kind for this display.</summary>
    public RotationSourceKind SourceKind
    {
        get => Session.SourceKind;
        set
        {
            if (Session.SourceKind == value) return;
            Session.SourceKind = value;
            OnPropertyChanged(nameof(SourceKind));
            OnPropertyChanged(nameof(SourceId));
            _owner.OnSourceChanged(this);
        }
    }

    /// <summary>Source id: collection name or feed id; null/"*" for Library.</summary>
    public string? SourceId
    {
        get => Session.SourceId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(Session.SourceId, normalized, StringComparison.Ordinal)) return;
            Session.SourceId = normalized;
            OnPropertyChanged(nameof(SourceId));
            _owner.OnSourceChanged(this);
        }
    }

    /// <summary>Choices for <see cref="SourceId"/> given the current kind.</summary>
    public IReadOnlyList<string> SourceIdOptions
    {
        get
        {
            return SourceKind switch
            {
                RotationSourceKind.Collection => _owner.AvailableCollections,
                RotationSourceKind.Feed => _owner.Feeds.Select(f => f.Id).ToArray(),
                _ => []
            };
        }
    }

    /// <summary>Kinds shown in the per-display source picker.</summary>
    public IReadOnlyList<RotationSourceKind> SourceKinds { get; } =
        [RotationSourceKind.Library, RotationSourceKind.Collection, RotationSourceKind.Feed];

    internal void RaiseSourceOptions() => OnPropertyChanged(nameof(SourceIdOptions));

    // ----- custom minutes -----

    public string CustomMinutesInput
    {
        get => _customMinutesInput;
        set => SetProperty(ref _customMinutesInput, value ?? string.Empty, nameof(CustomMinutesInput));
    }

    public string CustomMinutesStatus
    {
        get => _customMinutesStatus;
        private set => SetProperty(ref _customMinutesStatus, value, nameof(CustomMinutesStatus));
    }

    internal void EnsureInterval(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_intervals.Contains(name, StringComparer.OrdinalIgnoreCase))
            _intervals.Add(name);
        OnPropertyChanged(nameof(Intervals));
    }

    internal void ApplyProjection(
        string heading, string subheading, RotationState rotationState, string countdown, string statusText,
        string pauseButtonText, bool canStart, bool canPause, bool canRotateNow, bool canUsePrevious,
        bool canStop, bool intervalEnabled)
    {
        Heading = heading;
        Subheading = subheading;
        RotationState = rotationState;
        Countdown = countdown;
        StatusText = statusText;
        PauseButtonText = pauseButtonText;
        CanStart = canStart;
        CanPause = canPause;
        CanRotateNow = canRotateNow;
        CanUsePrevious = canUsePrevious;
        CanStop = canStop;
        IntervalEnabled = intervalEnabled;
        OnPropertyChanged(nameof(SelectedInterval));
        OnPropertyChanged(nameof(PauseWhenFullscreen));
        OnPropertyChanged(nameof(SourceKind));
        OnPropertyChanged(nameof(SourceId));
    }
}
