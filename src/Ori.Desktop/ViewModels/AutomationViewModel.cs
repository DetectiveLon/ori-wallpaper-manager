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
/// Independent app-session wallpaper rotation schedules for each connected display.
/// Scheduled changes normally use only files already stored in the local library;
/// optional, opt-in Wallhaven auto-fetch tops the library up when it runs dry. All
/// persistence and timing mechanics are delegated to the Core services.
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

    internal const string ReadyStatus = "Ready. Start this display's schedule when you are.";
    private const string EmptyLibraryStatus = "Save wallpapers from Discover to enable Start and Rotate now.";
    private const int MaximumFeedItemsPerTick = 3;

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
    private readonly Dictionary<string, DisplayScheduleViewModel> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _pauseWhenFullscreen = true;
    private bool _autoFetchFromWallhaven;
    private bool _shutdownRequested;
    private bool _libraryHasImages;
    private bool _deferredSchedulesExist;
    private bool _initialized;
    private string _globalStatus = "Preparing…";
    private string _libraryStatus = string.Empty;
    private string _displayStatus = string.Empty;
    private string _feedStatus = string.Empty;
    private string? _lastTrayTooltip;
    private DateTimeOffset? _lastFeedFetchUtc;
    private DateTimeOffset _feedBackoffUntilUtc;
    private int _feedConsecutiveFailures;

    public AutomationViewModel()
    {
        // Constructed by the view on the UI thread; capture its dispatcher so every
        // callback from the rotation loops can marshal state changes back to XAML.
        _dispatcher = Dispatcher.CurrentDispatcher;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiTimer.Tick += (_, _) => UpdateAllSessions();
        // Refresh the library too: after wallpapers are saved from Discover, the
        // Start/Rotate now buttons must become enabled again from this page.
        RefreshDisplaysCommand = new RelayCommand(_ => { _ = RefreshAvailabilityAsync(); });

        // The full-screen pause master is a user preference, not per-display state:
        // restore it here so it survives a restart. The store swallows its own I/O
        // errors, so even a damaged preferences file cannot block the page.
        _pauseWhenFullscreen = AutomationPreferencesStore.LoadPauseWhenFullscreen();

        try
        {
            _autoFetchFromWallhaven = WallhavenFeedStore.IsAutoFetchEnabled();
        }
        catch
        {
            // A damaged preference file must never stop the page from loading.
            _autoFetchFromWallhaven = false;
        }
    }

    private async Task RefreshAvailabilityAsync()
    {
        try
        {
            await RefreshLibraryStatusAsync();
            await RefreshDisplaysAsync();
        }
        catch (Exception ex)
        {
            // Called from a fire-and-forget command, so nothing else observes a throw.
            SetLibraryStatus($"Could not refresh automation state: {ex.Message}");
        }
    }

    private void SetLibraryStatus(string message)
    {
        _libraryStatus = message;
        UpdateGlobalStatus();
    }

    /// <summary>
    /// Reports a page-load failure in place. The view calls this from its async void
    /// Loaded handler, where an escaping exception would become an error toast.
    /// </summary>
    internal void ReportLoadFailure(Exception exception) =>
        SetLibraryStatus($"Could not load automation settings: {exception.Message}");

    private void SetDisplayStatus(string message)
    {
        _displayStatus = message;
        UpdateGlobalStatus();
    }

    private void SetFeedStatus(string message)
    {
        void apply()
        {
            _feedStatus = message;
            UpdateGlobalStatus();
        }

        if (_dispatcher.CheckAccess())
            apply();
        else
            _ = _dispatcher.InvokeAsync(apply);
    }

    private void UpdateGlobalStatus()
    {
        // The library message explains why Start/Rotate are disabled, so keep it
        // visible instead of letting the (later) display count overwrite it.
        var parts = new[] { _libraryStatus, _displayStatus, _feedStatus }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        GlobalStatus = string.Join(' ', parts);
    }

    /// <summary>One row per connected display, rendered by the AutomationView DataTemplate.</summary>
    public ObservableCollection<DisplayScheduleViewModel> Schedules { get; } = new();

    public string GlobalStatus
    {
        get => _globalStatus;
        private set => SetProperty(ref _globalStatus, value, nameof(GlobalStatus));
    }

    /// <summary>
    /// Global master switch for full-screen pausing. Each display also has its own
    /// opt-out checkbox; a display pauses only when both flags are set.
    /// </summary>
    public bool PauseWhenFullscreen
    {
        get => Volatile.Read(ref _pauseWhenFullscreen);
        set
        {
            if (!SetProperty(ref _pauseWhenFullscreen, value, nameof(PauseWhenFullscreen)))
                return;

            AutomationPreferencesStore.SavePauseWhenFullscreen(value);
            UpdateAllSessions();
        }
    }

    /// <summary>
    /// Opt-in for hands-free Wallhaven fetching. Defaults to off; saved so the choice
    /// survives a restart. No network request happens until Discover saves a feed.
    /// </summary>
    public bool AutoFetchFromWallhaven
    {
        get => Volatile.Read(ref _autoFetchFromWallhaven);
        set
        {
            if (!SetProperty(ref _autoFetchFromWallhaven, value, nameof(AutoFetchFromWallhaven)))
                return;

            try
            {
                WallhavenFeedStore.SetAutoFetchEnabled(value);
            }
            catch
            {
                // Persistence is a convenience; the in-memory toggle still applies.
            }

            SetFeedStatus(value
                ? "Auto-fetch is on. New Wallhaven images are added only when this display has no unseen local wallpapers left."
                : string.Empty);
        }
    }

    public ICommand RefreshDisplaysCommand { get; }

    /// <summary>
    /// Called from the view's Loaded handler. The view is cached, so this runs on
    /// every visit: availability is re-read each time while schedule restore and
    /// the UI ticker stay one-shot.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_shutdownRequested)
            return;

        var firstLoad = !_initialized;
        if (firstLoad)
            _initialized = true;

        await RefreshLibraryStatusAsync();
        // InitializeAsync owns the one-time full restore below, so suppress the
        // per-refresh restore here (every display is "new" on the first pass).
        await RefreshDisplaysAsync(restoreNewDisplays: false);

        if (!firstLoad)
        {
            // A schedule deferred on an earlier visit (empty library) must start as
            // soon as wallpapers become available, otherwise the "waiting for
            // wallpapers" message is a promise the app never keeps.
            if (_deferredSchedulesExist && _libraryHasImages)
                await RestoreSchedulesAsync();
            return;
        }

        await RestoreSchedulesAsync();
        if (!_uiTimer.IsEnabled)
            _uiTimer.Start();
    }

    /// <summary>
    /// Signals every loop to stop without waiting. Safe to call before StopAsync.
    /// </summary>
    public void RequestStop()
    {
        _shutdownRequested = true;
        _uiTimer.Stop();

        foreach (var session in _sessions.Values)
        {
            session.UserPaused = false;
            session.LoopCancellation?.Cancel();
            session.ManualCancellation?.Cancel();
        }
    }

    /// <summary>
    /// Bounded shutdown: requests cancellation of every rotation loop and waits at
    /// most three seconds for them to unwind. The integrator may call this directly
    /// from MainWindow.Closing (await it, or .Wait(TimeSpan.FromSeconds(3))).
    /// </summary>
    public async Task StopAsync()
    {
        RequestStop();

        var tasks = _sessions.Values.Select(session => StopSessionAsync(session)).ToArray();
        if (tasks.Length == 0)
            return;

        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all, Task.Delay(ShutdownTimeout));
    }

    private async Task PersistSessionAsync(DisplayScheduleViewModel session, bool? shouldRun = null)
    {
        if (_shutdownRequested)
            return;

        var states = _sessions.Values.Select(current => new DisplayScheduleState(
            current.Monitor.Id,
            current.SelectedInterval,
            ReferenceEquals(current, session) && shouldRun.HasValue
                ? shouldRun.Value
                : current.ActiveSchedule,
            current.UserPaused,
            current.NextRunUtc,
            current.LastAppliedId,
            current.PauseWhenFullscreen,
            current.Fingerprint)).ToArray();

        await SaveMergedStatesAsync(states);
    }

    /// <summary>
    /// Writes only the supplied states over the saved file, keeping every saved entry
    /// that has no live session. A display that was unplugged is deliberately absent
    /// from <see cref="_sessions"/>: its schedule must stay on disk so a later
    /// fingerprint match can adopt it. This also stops one session's save from
    /// replacing another session's saved interval/pause/deadline with defaults.
    /// </summary>
    private async Task SaveMergedStatesAsync(
        IEnumerable<DisplayScheduleState> overrides,
        IEnumerable<string>? removeIds = null)
    {
        try
        {
            await _persistGate.WaitAsync();
            try
            {
                IReadOnlyDictionary<string, DisplayScheduleState> saved;
                try
                {
                    saved = await _schedules.LoadAsync();
                }
                catch
                {
                    saved = new Dictionary<string, DisplayScheduleState>(StringComparer.OrdinalIgnoreCase);
                }

                var merged = new Dictionary<string, DisplayScheduleState>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in saved)
                    merged[pair.Key] = pair.Value;

                if (removeIds is not null)
                {
                    foreach (var id in removeIds)
                        merged.Remove(id);
                }

                foreach (var state in overrides)
                    merged[state.MonitorId] = state;

                await _schedules.SaveAsync(merged.Values);
            }
            finally
            {
                _persistGate.Release();
            }
        }
        catch
        {
            // A failed persistence write must never stop the active schedule.
        }
    }

    /// <summary>
    /// Restores saved schedules onto the current sessions. When <paramref name="onlySessions"/>
    /// is supplied (a display appeared after an in-app refresh), adoption still considers
    /// every live display so a colliding fingerprint cannot steal another display's
    /// schedule, but only the new displays are started.
    /// </summary>
    private async Task RestoreSchedulesAsync(
        IReadOnlyCollection<DisplayScheduleViewModel>? onlySessions = null)
    {
        if (_shutdownRequested)
            return;

        if (onlySessions is null)
            _deferredSchedulesExist = false;

        IReadOnlyDictionary<string, DisplayScheduleState> saved;
        try
        {
            saved = await _schedules.LoadAsync();
        }
        catch
        {
            return;
        }

        var scope = onlySessions is null
            ? null
            : new HashSet<DisplayScheduleViewModel>(onlySessions);
        var byId = new Dictionary<string, DisplayScheduleState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in saved)
            byId[pair.Key] = pair.Value;

        var resolved = new Dictionary<DisplayScheduleViewModel, DisplayScheduleState>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adoptedFrom = new List<string>();
        var adopted = false;
        var identityDirty = false;

        // Pass one: a saved entry whose id matches a live display belongs to that
        // display. Claim every such entry before any adoption so a newer session
        // cannot steal it just because it is enumerated first.
        foreach (var session in _sessions.Values)
        {
            var fingerprint = session.Fingerprint;
            if (!byId.TryGetValue(session.Monitor.Id, out var direct))
                continue;

            claimed.Add(session.Monitor.Id);
            if (!string.Equals(direct.MonitorFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // Same COM path but moved/resized: the identity must be written back
                // or a later path change will no longer match by fingerprint.
                direct = direct with { MonitorFingerprint = fingerprint };
                byId[session.Monitor.Id] = direct;
                identityDirty = true;
            }

            resolved[session] = direct;
        }

        // Pass two: adopt only saved entries that no live display is already using.
        foreach (var session in _sessions.Values)
        {
            if (resolved.ContainsKey(session))
                continue;

            var fingerprint = session.Fingerprint;
            string? matchId = null;
            DisplayScheduleState? matchState = null;
            foreach (var pair in byId)
            {
                if (claimed.Contains(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value.MonitorFingerprint)
                    || !string.Equals(pair.Value.MonitorFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchId = pair.Key;
                matchState = pair.Value;
                break;
            }

            if (matchId is null || matchState is null)
                continue;

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

        var restored = new List<(DisplayScheduleViewModel Session, DisplayScheduleState State)>();
        foreach (var session in _sessions.Values)
        {
            if (resolved.TryGetValue(session, out var state) && (scope is null || scope.Contains(session)))
                restored.Add((session, state));
        }

        // Apply every restored state in memory first, then write them all at once.
        // Persisting one display before the next has been restored is what used to
        // overwrite the second display's interval/pause/deadline with defaults.
        var desired = new List<DisplayScheduleState>(restored.Count);
        foreach (var (session, state) in restored)
        {
            if (IntervalsByName.ContainsKey(state.IntervalName))
                session.SelectedInterval = state.IntervalName;
            session.LastAppliedId = state.LastAppliedId;
            session.ApplyRestoredPausePolicy(state.PauseWhenFullscreen);

            // Populate the whole session before any loop starts: the first
            // StartRotationAsync persists every live session, so a later display
            // must already hold its saved deadline/pause/ShouldRun in memory.
            if (!IsRunning(session))
            {
                session.UserPaused = state.UserPaused;
                session.NextRunUtc = state.NextRunUtc;
                session.ActiveSchedule = state.ShouldRun;
            }

            desired.Add(state with
            {
                MonitorId = session.Monitor.Id,
                MonitorFingerprint = session.Fingerprint,
                IntervalName = session.SelectedInterval,
                PauseWhenFullscreen = session.PauseWhenFullscreen,
                LastAppliedId = session.LastAppliedId,
                UserPaused = session.UserPaused,
                NextRunUtc = session.NextRunUtc,
                ShouldRun = session.ActiveSchedule
            });
        }

        // A single merged save covers restored intervals/pauses/deadlines, newly
        // adopted ids and fingerprints refreshed after a geometry change.
        if (adopted || identityDirty || desired.Count > 0)
            await SaveMergedStatesAsync(desired, adoptedFrom);

        foreach (var (session, state) in restored)
        {
            if (!state.ShouldRun)
            {
                UpdateSessionUi(session);
                continue;
            }

            if (!_libraryHasImages)
            {
                _deferredSchedulesExist = true;
                session.StatusMessage = "Saved schedule is waiting for wallpapers in the local library.";
                UpdateSessionUi(session);
                continue;
            }

            await StartRotationAsync(session, state.NextRunUtc, restoring: true);
        }
    }

    private async Task RefreshLibraryStatusAsync()
    {
        if (_shutdownRequested)
            return;

        try
        {
            var entries = await _library.GetAllAsync();
            _libraryHasImages = entries.Count > 0;
            SetLibraryStatus(entries.Count == 0
                ? "Your local library is empty. Save wallpapers from Discover before starting a schedule."
                : $"{entries.Count} wallpaper(s) are saved locally and available for offline rotation.");
        }
        catch (Exception ex)
        {
            _libraryHasImages = false;
            SetLibraryStatus($"Could not read the local library: {ex.Message}");
        }

        UpdateAllSessions();
    }

    internal async Task RefreshDisplaysAsync(bool restoreNewDisplays = true)
    {
        if (_shutdownRequested)
            return;

        try
        {
            var detection = DisplayDetection.Read();
            if (!detection.Succeeded)
            {
                SetDisplayStatus($"Could not refresh displays: {detection.Error}");
                return;
            }

            var monitors = detection.Monitors;
            var connectedIds = monitors
                .Select(monitor => monitor.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var removed in _sessions.Values
                         .Where(session => !connectedIds.Contains(session.Monitor.Id))
                         .ToArray())
            {
                // Suspend, do not stop: the loop is cancelled in memory but the saved
                // ShouldRun/interval/pause/deadline stay on disk. Writing false here
                // would make fingerprint adoption impossible when the panel returns.
                await StopSessionAsync(removed, keepSavedSchedule: true);
                Schedules.Remove(removed);
                _sessions.Remove(removed.Monitor.Id);
            }

            var added = new List<DisplayScheduleViewModel>();
            foreach (var monitor in monitors)
            {
                if (_sessions.TryGetValue(monitor.Id, out var existing))
                {
                    existing.Monitor = monitor;
                    UpdateSessionUi(existing);
                    continue;
                }

                var session = new DisplayScheduleViewModel(monitor, this);
                _sessions.Add(monitor.Id, session);
                Schedules.Add(session);
                UpdateSessionUi(session);
                added.Add(session);
            }

            SetDisplayStatus(monitors.Count == 0
                ? "Windows did not report any connected displays."
                : $"{monitors.Count} display(s) detected. Start a schedule on each display you want Ori to manage.");

            // A display that appeared after an in-app change (replug, driver
            // re-enumeration) must restore its saved schedule now, not after a restart.
            if (restoreNewDisplays && added.Count > 0)
                await RestoreSchedulesAsync(added);
        }
        catch (Exception ex)
        {
            SetDisplayStatus($"Could not refresh displays: {ex.Message}");
        }
    }

    internal async Task StartRotationAsync(
        DisplayScheduleViewModel session,
        DateTimeOffset? resumeAt = null,
        bool restoring = false)
    {
        if (_shutdownRequested || session.IsStarting || IsRunning(session))
            return;

        if (!IntervalsByName.TryGetValue(session.SelectedInterval, out var interval))
        {
            session.StatusMessage = "Choose a valid rotation interval.";
            UpdateSessionUi(session);
            return;
        }

        session.IsStarting = true;
        UpdateSessionUi(session);

        try
        {
            var entries = await _library.GetAllAsync();
            if (_shutdownRequested)
                return;

            var available = entries.Where(entry => File.Exists(entry.LocalPath)).ToArray();
            if (available.Length == 0)
            {
                session.StatusMessage = "No available images in the library. Save wallpapers from Discover first.";
                return;
            }

            session.Loop = new RotationLoop();
            session.LoopCancellation = new CancellationTokenSource();
            session.ActiveSchedule = true;
            if (!restoring)
                session.UserPaused = false;
            session.NextRunUtc = resumeAt ?? DateTimeOffset.UtcNow + interval;
            session.StatusMessage = restoring
                ? "Schedule restored. Rotation continues from the saved deadline."
                : $"{available.Length} local wallpaper(s) available. This display's schedule is running.";

            var token = session.LoopCancellation.Token;
            var task = session.Loop.RunAsync(
                interval,
                shouldPause: () => ShouldPause(session),
                rotateAsync: cancellationToken => RotateFromLibraryAsync(
                    session,
                    cancellationToken,
                    respectPause: true,
                    expectedManualRotationGeneration: Volatile.Read(ref session.ManualRotationGeneration)),
                nextRunChanged: async nextRun =>
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        session.NextRunUtc = nextRun;
                        UpdateSessionUi(session);
                    });
                    await PersistSessionAsync(session);
                },
                failed: async exception =>
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        session.StatusMessage = $"Rotation failed: {exception.Message}";
                        UpdateSessionUi(session);
                    });
                },
                cancellationToken: token,
                lastCompletedAt: resumeAt.HasValue ? resumeAt.Value - interval : null);

            session.LoopTask = task;
            _ = ObserveLoopAsync(session, task);
            await PersistSessionAsync(session, shouldRun: true);
        }
        catch (Exception ex)
        {
            session.StatusMessage = $"Could not start rotation: {ex.Message}";
            await StopSessionAsync(session);
            // StopSessionAsync resets the status to "Schedule stopped."; the start error
            // is the useful diagnostic and must survive the cleanup.
            session.StatusMessage = $"Could not start rotation: {ex.Message}";
            UpdateSessionUi(session);
        }
        finally
        {
            session.IsStarting = false;
            UpdateSessionUi(session);
        }
    }

    private async Task ObserveLoopAsync(DisplayScheduleViewModel session, Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            session.StatusMessage = $"Rotation stopped: {ex.Message}";
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted && Application.Current is not null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(session.LoopTask, task))
                        FinishLoop(session, task);
                });
            }
        }
    }

    private void FinishLoop(DisplayScheduleViewModel session, Task task)
    {
        if (!ReferenceEquals(session.LoopTask, task))
            return;

        session.LoopTask = null;
        session.LoopCancellation?.Dispose();
        session.LoopCancellation = null;
        session.NextRunUtc = null;
        session.UserPaused = false;
        UpdateSessionUi(session);
    }

    private bool ShouldPause(DisplayScheduleViewModel session) =>
        session.UserPaused
        || (Volatile.Read(ref _pauseWhenFullscreen)
            && session.PauseWhenFullscreen
            && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor));

    /// <summary>Called by the per-card full-screen pause checkbox's setter.</summary>
    internal void OnSessionFullscreenPauseChanged(DisplayScheduleViewModel session)
    {
        UpdateSessionUi(session);
        _ = PersistSessionAsync(session);
    }

    internal void TogglePause(DisplayScheduleViewModel session)
    {
        if (!IsRunning(session))
            return;

        session.UserPaused = !session.UserPaused;
        session.StatusMessage = session.UserPaused ? "Paused manually." : "Manual pause released.";
        UpdateSessionUi(session);
        _ = PersistSessionAsync(session);
    }

    internal async Task RotateNowAsync(DisplayScheduleViewModel session)
    {
        if (_shutdownRequested || session.IsStarting || session.ManualRotationRunning)
            return;

        session.ManualRotationRunning = true;
        var loopToken = session.LoopCancellation?.Token ?? CancellationToken.None;
        var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        session.ManualCancellation = manualCancellation;
        UpdateSessionUi(session);

        var operation = RotateFromLibraryAsync(
            session,
            manualCancellation.Token,
            respectPause: false);
        session.ManualTask = operation;

        try
        {
            await operation;
            Interlocked.Increment(ref session.ManualRotationGeneration);
            if (IsRunning(session))
                session.Loop.ResetNextRun();
        }
        catch (OperationCanceledException) when (manualCancellation.IsCancellationRequested)
        {
            session.StatusMessage = "Manual rotation was cancelled.";
        }
        catch (Exception ex)
        {
            session.StatusMessage = $"Could not rotate this display: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(session.ManualCancellation, manualCancellation))
                session.ManualCancellation = null;
            session.ManualTask = null;
            manualCancellation.Dispose();
            session.ManualRotationRunning = false;
            UpdateSessionUi(session);
        }
    }

    /// <summary>
    /// Re-applies this display's previous wallpaper and restarts the interval. A click
    /// with no history is reported inline instead of silently doing nothing.
    /// </summary>
    internal async Task PreviousAsync(DisplayScheduleViewModel session)
    {
        if (_shutdownRequested || session.IsStarting
            || session.PreviousRotationRunning || session.ManualRotationRunning)
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

                    // Swap history so repeated clicks toggle between the two images.
                    session.PreviousAppliedPath = session.LastAppliedPath;
                    session.LastAppliedPath = previousPath;
                    session.LastAppliedId = matchedId;
                    session.Monitor = monitor with { CurrentWallpaper = previousPath };
                    session.StatusMessage = $"Restored the previous wallpaper on {monitor.DeviceName}.";
                    UpdateSessionUi(session);
                });
            }
            finally
            {
                _rotationGate.Release();
            }

            if (IsRunning(session))
                session.Loop.ResetNextRun();
            await PersistSessionAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.StatusMessage = "Returning to the previous wallpaper was cancelled.";
        }
        catch (Exception ex)
        {
            session.StatusMessage = $"Could not restore the previous wallpaper: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(session.ManualCancellation, linked))
                session.ManualCancellation = null;
            session.PreviousRotationRunning = false;
            UpdateSessionUi(session);
        }
    }

    private async Task RotateFromLibraryAsync(
        DisplayScheduleViewModel session,
        CancellationToken cancellationToken,
        bool respectPause,
        int? expectedManualRotationGeneration = null)
    {
        await _rotationGate.WaitAsync(cancellationToken);
        try
        {
            if (expectedManualRotationGeneration is int expectedBeforeLoad
                && expectedBeforeLoad != Volatile.Read(ref session.ManualRotationGeneration))
            {
                return;
            }

            // Top the library up only when this display has nothing unseen left.
            await EnsureFeedWallpapersAsync(session, cancellationToken).ConfigureAwait(false);

            var entries = await _library.GetAllAsync(cancellationToken);
            var available = entries.Where(entry => File.Exists(entry.LocalPath)).ToArray();
            if (available.Length == 0)
                throw new InvalidOperationException("No available images are saved in the local library.");

            var monitorAtSelection = session.Monitor;
            var lastId = session.LastAppliedId;
            if (lastId is null && !string.IsNullOrWhiteSpace(monitorAtSelection.CurrentWallpaper))
            {
                lastId = available.FirstOrDefault(entry =>
                    PathsEqual(entry.LocalPath, monitorAtSelection.CurrentWallpaper))?.Id;
            }

            var candidates = available.Length > 1
                ? available.Where(entry => !string.Equals(
                    entry.Id, lastId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : available;
            var selected = candidates[Random.Shared.Next(candidates.Length)];

            await _dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (expectedManualRotationGeneration is int expectedBeforeApply
                    && expectedBeforeApply != Volatile.Read(ref session.ManualRotationGeneration))
                {
                    session.StatusMessage = "Scheduled change skipped because a manual rotation just completed.";
                    UpdateSessionUi(session);
                    return;
                }

                // Recheck immediately before touching the desktop: pause or full-screen
                // state may have changed while the library was being read.
                if (respectPause && ShouldPause(session))
                {
                    session.StatusMessage = "Scheduled change deferred because this display is paused.";
                    UpdateSessionUi(session);
                    return;
                }

                if (!File.Exists(selected.LocalPath))
                    throw new FileNotFoundException("The selected library image is missing.", selected.LocalPath);

                // A display refresh (or a newer manual rotation) can replace the
                // monitor snapshot while the library was being read. Applying to the
                // stale id would hit a dead path and overwrite the newer snapshot.
                if (!ReferenceEquals(session.Monitor, monitorAtSelection))
                {
                    session.StatusMessage = "Scheduled change skipped: this display changed during rotation.";
                    UpdateSessionUi(session);
                    return;
                }

                var monitor = session.Monitor;
                WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, selected.LocalPath);

                // Keep a one-deep history of applied paths for the Previous command.
                var outgoing = session.LastAppliedPath;
                if (!string.IsNullOrWhiteSpace(outgoing) && !PathsEqual(outgoing, selected.LocalPath))
                    session.PreviousAppliedPath = outgoing;
                session.LastAppliedPath = selected.LocalPath;

                session.LastAppliedId = selected.Id;
                session.Monitor = monitor with { CurrentWallpaper = selected.LocalPath };
                session.StatusMessage = $"Applied {selected.Title} to {monitor.DeviceName}.";
                UpdateSessionUi(session);
            });
            await PersistSessionAsync(session);
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    /// <summary>
    /// Opt-in Wallhaven feed top-up. Runs off-thread inside the rotation gate, respects
    /// the loop token, and never lets a network failure escape to stop the loop.
    /// </summary>
    private async Task EnsureFeedWallpapersAsync(
        DisplayScheduleViewModel session,
        CancellationToken cancellationToken)
    {
        if (_shutdownRequested || !Volatile.Read(ref _autoFetchFromWallhaven))
            return;

        (WallhavenFilters Filters, int CadenceMinutes)? feed;
        try
        {
            feed = WallhavenFeedStore.LoadWithCadence();
        }
        catch
        {
            feed = null;
        }

        if (feed is null)
        {
            SetFeedStatus("Auto-fetch is on, but no Discover search has been saved yet.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _feedBackoffUntilUtc)
            return;
        if (_lastFeedFetchUtc is { } last
            && now - last < TimeSpan.FromMinutes(feed.Value.CadenceMinutes))
        {
            return;
        }

        var entries = await _library.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var unseen = entries.Any(entry =>
            !string.Equals(entry.Id, session.LastAppliedId, StringComparison.OrdinalIgnoreCase)
            && File.Exists(entry.LocalPath));
        if (unseen)
            return;

        _lastFeedFetchUtc = now;
        SetFeedStatus("No unseen local wallpapers left. Fetching from your saved Discover filters…");

        try
        {
            // Named arguments keep the call valid against the client's existing
            // (filters, apiKey, cancellationToken) signature. The key comes from the
            // DPAPI-protected store; nothing is hardcoded here.
            string apiKey;
            try
            {
                apiKey = ApiKeyStore.Load();
            }
            catch
            {
                apiKey = string.Empty;
            }

            var page = await FeedClient.SearchAsync(
                feed.Value.Filters, apiKey: apiKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var sourceId = TryReadProperty(entry, "SourceId") as string;
                if (!string.IsNullOrWhiteSpace(sourceId))
                    knownIds.Add(sourceId);
                if (!string.IsNullOrWhiteSpace(entry.ImageUrl))
                    knownUrls.Add(entry.ImageUrl);
                if (!string.IsNullOrWhiteSpace(entry.PageUrl))
                    knownUrls.Add(entry.PageUrl);
                knownIds.Add(entry.Id);
            }

            var added = 0;
            foreach (var wallpaper in page.Items.Take(MaximumFeedItemsPerTick))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (knownIds.Contains(wallpaper.Id)
                    || knownUrls.Contains(wallpaper.ImageUrl.ToString())
                    || knownUrls.Contains(wallpaper.PageUrl.ToString()))
                {
                    continue;
                }

                var path = await FeedImages.SaveToLibraryAsync(wallpaper, cancellationToken).ConfigureAwait(false);
                var entry = new WallpaperLibraryEntry
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
                };

                // Optional metadata added by the Discover agent. Reflection keeps this
                // loop compiling and running even if an older entry type is in use.
                TryWriteProperty(entry, "SourceId", wallpaper.Id);
                TryWriteProperty(entry, "SourceQuery", feed.Value.Filters.Query);
                // WallpaperLibraryEntry.Colors is a comma-joined string, not a collection.
                TryWriteProperty(entry, "Colors", string.Join(", ", wallpaper.Colors));

                await _library.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                knownIds.Add(wallpaper.Id);
                knownUrls.Add(wallpaper.ImageUrl.ToString());
                knownUrls.Add(wallpaper.PageUrl.ToString());
                added++;
            }

            _feedConsecutiveFailures = 0;
            var message = added == 0
                ? "Wallhaven had no new wallpapers matching your Discover filters."
                : $"Added {added} new Wallhaven wallpaper(s) to the local library.";
            SetFeedStatus(message);
            if (added > 0)
                NotifyTray("Ori · auto-fetch", message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (IsRateLimit(ex))
        {
            _feedConsecutiveFailures++;
            var seconds = Math.Min(30L * (1L << Math.Min(_feedConsecutiveFailures - 1, 5)), 900L);
            _feedBackoffUntilUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
            var message = $"Wallhaven rate limit reached; auto-fetch retries in about {seconds} seconds.";
            SetFeedStatus(message);
            NotifyTray("Ori · auto-fetch", message, error: true);
        }
        catch (Exception ex)
        {
            // A network/download failure is reported but must never kill the loop.
            _feedConsecutiveFailures++;
            var message = $"Auto-fetch could not add wallpapers: {ex.Message}";
            SetFeedStatus(message);
            NotifyTray("Ori · auto-fetch", message, error: true);
        }
    }

    private static bool IsRateLimit(HttpRequestException exception)
    {
        if (exception.StatusCode is { } status && (int)status == 429)
            return true;
        return exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    private static object? TryReadProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteProperty(object target, string name, object? value)
    {
        try
        {
            var property = target.GetType().GetProperty(name);
            if (property is { CanWrite: true } && value is not null)
                property.SetValue(target, value);
        }
        catch
        {
            // The optional metadata field is absent or has a different shape; skip it.
        }
    }

    private static void NotifyTray(string title, string message, bool error = false)
    {
        try
        {
            App.Tray?.Notify(title, message, error);
        }
        catch
        {
            // A missing or busy tray icon must never break rotation.
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cancels a display's loops. With <paramref name="keepSavedSchedule"/> the saved
    /// schedule is left untouched: used when a display disappears, so a later
    /// fingerprint match can revive the schedule instead of seeing ShouldRun=false.
    /// </summary>
    internal async Task StopSessionAsync(
        DisplayScheduleViewModel session,
        bool keepSavedSchedule = false)
    {
        var loopCancellation = session.LoopCancellation;
        var loopTask = session.LoopTask;
        var manualTask = session.ManualTask;

        session.ActiveSchedule = false;
        session.ManualCancellation?.Cancel();
        loopCancellation?.Cancel();

        if (manualTask is not null)
        {
            try
            {
                await manualTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                session.StatusMessage = $"Manual rotation stopped: {ex.Message}";
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                session.StatusMessage = $"Rotation stopped: {ex.Message}";
            }
        }

        if (ReferenceEquals(session.LoopTask, loopTask) && loopTask is not null)
            FinishLoop(session, loopTask);

        if (ReferenceEquals(session.LoopCancellation, loopCancellation))
        {
            session.LoopCancellation?.Dispose();
            session.LoopCancellation = null;
        }

        session.UserPaused = false;
        session.NextRunUtc = null;
        if (!_shutdownRequested)
        {
            session.StatusMessage = "Schedule stopped.";
            UpdateSessionUi(session);
        }
        if (!keepSavedSchedule)
            await PersistSessionAsync(session, shouldRun: false);
    }

    private static bool IsRunning(DisplayScheduleViewModel session) =>
        session.LoopTask is { IsCompleted: false };

    private void UpdateAllSessions()
    {
        foreach (var session in _sessions.Values)
            UpdateSessionUi(session);
    }

    private void UpdateSessionUi(DisplayScheduleViewModel session)
    {
        var running = IsRunning(session);
        var fullscreenPauseAllowed = Volatile.Read(ref _pauseWhenFullscreen) && session.PauseWhenFullscreen;
        session.FullscreenPaused = running
            && fullscreenPauseAllowed
            && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor);

        session.Heading = session.Monitor.IsPrimary
            ? $"{session.Monitor.DeviceName}  ·  PRIMARY"
            : session.Monitor.DeviceName;
        session.Subheading =
            $"{session.Monitor.Width:N0} × {session.Monitor.Height:N0}  ·  Position {session.Monitor.Left}, {session.Monitor.Top}";

        if (!running || session.NextRunUtc is null)
        {
            if (session.IsStarting)
            {
                session.Countdown = "Preparing…";
                session.RotationState = RotationState.Preparing;
            }
            else if (session.ManualRotationRunning || session.PreviousRotationRunning)
            {
                session.Countdown = "Applying…";
                session.RotationState = RotationState.Applying;
            }
            else
            {
                session.Countdown = "Not running";
                session.RotationState = RotationState.Idle;
            }
        }
        else if (session.UserPaused || session.FullscreenPaused)
        {
            session.Countdown = "Paused";
            session.RotationState = RotationState.Paused;
        }
        else
        {
            session.RotationState = RotationState.Running;
            var remaining = session.NextRunUtc.Value - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            var days = (int)remaining.TotalDays;
            session.Countdown = days > 0
                ? $"{days}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        session.StatusText = ResolveStatusText(session, running, !_libraryHasImages);

        var busy = running || session.IsStarting || session.ManualRotationRunning || session.PreviousRotationRunning;
        session.CanStart = !busy && !_shutdownRequested && _libraryHasImages;
        session.CanPause = running;
        session.PauseButtonText = session.UserPaused ? "Resume" : "Pause";
        session.CanRotateNow = !session.IsStarting
            && !session.ManualRotationRunning
            && !session.PreviousRotationRunning
            && !_shutdownRequested
            && _libraryHasImages;
        session.CanStop = running || session.IsStarting || session.ManualRotationRunning || session.PreviousRotationRunning;
        session.IntervalEnabled = !busy;

        UpdateTrayTooltip();
    }

    private static string ResolveStatusText(DisplayScheduleViewModel session, bool running, bool libraryEmpty)
    {
        if (session.UserPaused)
            return "Paused manually. Resume to restart the full interval.";
        if (session.FullscreenPaused)
            return "Paused: a full-screen foreground window is active on this display. The full interval restarts when it closes.";
        // An idle card with an empty library must explain why Start/Rotate are off,
        // instead of showing the generic "Ready" text.
        if (libraryEmpty && !running)
            return EmptyLibraryStatus;
        return session.StatusMessage;
    }

    /// <summary>Keeps the tray tooltip showing the earliest upcoming change.</summary>
    private void UpdateTrayTooltip()
    {
        try
        {
            var next = _sessions.Values
                .Where(session => IsRunning(session)
                                  && session.NextRunUtc is not null
                                  && !session.UserPaused
                                  && !session.FullscreenPaused)
                .Select(session => session.NextRunUtc)
                .Where(value => value is not null)
                .OrderBy(value => value)
                .FirstOrDefault();

            var text = next is { } deadline
                ? $"Ori — next change {deadline.ToLocalTime():HH:mm}"
                : "Ori — wallpaper manager";
            if (string.Equals(text, _lastTrayTooltip, StringComparison.Ordinal))
                return;

            _lastTrayTooltip = text;
            App.Tray?.UpdateTooltip(text);
        }
        catch
        {
            // The tray is optional; a tooltip failure must never affect rotation.
        }
    }

    /// <summary>
    /// Tiny local preference store for the global full-screen pause master. Per-display
    /// flags live in the schedule file; the master is app-wide, so it gets its own
    /// record instead of being smuggled into a monitor's schedule.
    /// </summary>
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
                    if (!File.Exists(PreferencesPath))
                        return true;

                    using var document = JsonDocument.Parse(File.ReadAllText(PreferencesPath));
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("pauseWhenFullscreen", out var flag))
                    {
                        return true;
                    }

                    return flag.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => true
                    };
                }
                catch
                {
                    // Missing or damaged preference: keep the safe default (on).
                    return true;
                }
            }
        }

        public static void SavePauseWhenFullscreen(bool enabled)
        {
            lock (Gate)
            {
                try
                {
                    var directory = Path.GetDirectoryName(PreferencesPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    var temporary = PreferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(
                            temporary,
                            "{\"version\":1,\"pauseWhenFullscreen\":" + (enabled ? "true" : "false") + "}");
                        File.Move(temporary, PreferencesPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                }
                catch
                {
                    // Best-effort persistence; the in-memory toggle still applies.
                }
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

/// <summary>Per-display row rendered by the AutomationView DataTemplate.</summary>
public sealed class DisplayScheduleViewModel : ViewModelBase
{
    private readonly AutomationViewModel _owner;

    private RotationState _rotationState = RotationState.Idle;
    private string _heading = string.Empty;
    private string _subheading = string.Empty;
    private string _selectedInterval = "30 minutes";
    private string _countdown = "Not running";
    private string _statusText = AutomationViewModel.ReadyStatus;
    private string _pauseButtonText = "Pause";
    private bool _canStart;
    private bool _canPause;
    private bool _canRotateNow;
    private bool _canStop;
    private bool _intervalEnabled = true;
    private bool _pauseWhenFullscreen = true;

    public DisplayScheduleViewModel(DisplayMonitor monitor, AutomationViewModel owner)
    {
        _owner = owner;
        Monitor = monitor;
        Intervals = AutomationViewModel.IntervalNames;

        StartCommand = new RelayCommand(_ => { _ = _owner.StartRotationAsync(this); });
        PauseCommand = new RelayCommand(_ => _owner.TogglePause(this));
        PreviousCommand = new RelayCommand(_ => { _ = _owner.PreviousAsync(this); });
        RotateNowCommand = new RelayCommand(_ => { _ = _owner.RotateNowAsync(this); });
        StopCommand = new RelayCommand(_ => { _ = _owner.StopSessionAsync(this); });
    }

    public IReadOnlyList<string> Intervals { get; }

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand RotateNowCommand { get; }
    public ICommand StopCommand { get; }

    /// <summary>
    /// Single source of truth for the state pill. The countdown remains a human-readable
    /// timer; views must not parse it to decide which state to show.
    /// </summary>
    public RotationState RotationState
    {
        get => _rotationState;
        internal set => SetProperty(ref _rotationState, value, nameof(RotationState));
    }

    public string Heading
    {
        get => _heading;
        internal set => SetProperty(ref _heading, value, nameof(Heading));
    }

    public string Subheading
    {
        get => _subheading;
        internal set => SetProperty(ref _subheading, value, nameof(Subheading));
    }

    public string SelectedInterval
    {
        get => _selectedInterval;
        set => SetProperty(ref _selectedInterval, value, nameof(SelectedInterval));
    }

    public string Countdown
    {
        get => _countdown;
        internal set => SetProperty(ref _countdown, value, nameof(Countdown));
    }

    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value, nameof(StatusText));
    }

    public string PauseButtonText
    {
        get => _pauseButtonText;
        internal set => SetProperty(ref _pauseButtonText, value, nameof(PauseButtonText));
    }

    /// <summary>Per-display opt-in for full-screen pause; the page checkbox is a master switch.</summary>
    public bool PauseWhenFullscreen
    {
        get => _pauseWhenFullscreen;
        set
        {
            if (SetProperty(ref _pauseWhenFullscreen, value, nameof(PauseWhenFullscreen)))
                _owner.OnSessionFullscreenPauseChanged(this);
        }
    }

    public bool CanStart
    {
        get => _canStart;
        internal set => SetProperty(ref _canStart, value, nameof(CanStart));
    }

    public bool CanPause
    {
        get => _canPause;
        internal set => SetProperty(ref _canPause, value, nameof(CanPause));
    }

    public bool CanRotateNow
    {
        get => _canRotateNow;
        internal set => SetProperty(ref _canRotateNow, value, nameof(CanRotateNow));
    }

    public bool CanStop
    {
        get => _canStop;
        internal set => SetProperty(ref _canStop, value, nameof(CanStop));
    }

    public bool IntervalEnabled
    {
        get => _intervalEnabled;
        internal set => SetProperty(ref _intervalEnabled, value, nameof(IntervalEnabled));
    }

    internal string Fingerprint => ScheduleStore.BuildFingerprint(
        Monitor.DeviceName, Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height);

    /// <summary>Restores the persisted per-display flag without re-triggering a save.</summary>
    internal void ApplyRestoredPausePolicy(bool enabled) =>
        SetProperty(ref _pauseWhenFullscreen, enabled, nameof(PauseWhenFullscreen));

    // Engine state, owned by AutomationViewModel and deliberately outside the
    // binding surface. Fields (not properties) preserve the original semantics.
    internal DisplayMonitor Monitor { get; set; }
    internal RotationLoop Loop = new();
    internal CancellationTokenSource? LoopCancellation;
    internal Task? LoopTask;
    internal CancellationTokenSource? ManualCancellation;
    internal Task? ManualTask;
    internal DateTimeOffset? NextRunUtc;
    internal volatile bool UserPaused;
    internal bool FullscreenPaused;
    internal bool IsStarting;
    internal bool ManualRotationRunning;
    internal bool PreviousRotationRunning;
    internal int ManualRotationGeneration;
    internal bool ActiveSchedule;
    internal string? LastAppliedId;
    internal string? LastAppliedPath;
    internal string? PreviousAppliedPath;
    internal string StatusMessage = AutomationViewModel.ReadyStatus;
}
