using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Oriole.Core.Models;
using Oriole.Core.Services;
using Oriole.Desktop.Mvvm;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.ViewModels;

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
        await RefreshLibraryStatusAsync();
        await RefreshDisplaysAsync();
    }

    private void SetLibraryStatus(string message)
    {
        _libraryStatus = message;
        UpdateGlobalStatus();
    }

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
            if (SetProperty(ref _pauseWhenFullscreen, value, nameof(PauseWhenFullscreen)))
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
        await RefreshDisplaysAsync();

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

        var tasks = _sessions.Values.Select(StopSessionAsync).ToArray();
        if (tasks.Length == 0)
            return;

        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all, Task.Delay(ShutdownTimeout));
    }

    private async Task PersistSessionAsync(DisplayScheduleViewModel session, bool? shouldRun = null)
    {
        if (_shutdownRequested)
            return;

        try
        {
            await _persistGate.WaitAsync();
            try
            {
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
                await _schedules.SaveAsync(states);
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

    private async Task RestoreSchedulesAsync()
    {
        if (_shutdownRequested)
            return;

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

        // Re-key by monitor ID and adopt any schedule whose stable fingerprint still
        // matches, so a driver/USB/GPU change does not silently stop the old schedule.
        var byId = new Dictionary<string, DisplayScheduleState>(saved, StringComparer.OrdinalIgnoreCase);
        var restored = new List<(DisplayScheduleViewModel Session, DisplayScheduleState State)>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adopted = false;

        foreach (var session in _sessions.Values)
        {
            var fingerprint = session.Fingerprint;
            if (!byId.TryGetValue(session.Monitor.Id, out var state))
            {
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

                state = matchState with
                {
                    MonitorId = session.Monitor.Id,
                    MonitorFingerprint = fingerprint
                };
                byId.Remove(matchId);
                byId[session.Monitor.Id] = state;
                claimed.Add(session.Monitor.Id);
                adopted = true;
            }
            else if (!string.Equals(state.MonitorFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // Same COM path but moved/resized: refresh the stored identity.
                state = state with { MonitorFingerprint = fingerprint };
                byId[session.Monitor.Id] = state;
            }

            restored.Add((session, state));
        }

        if (adopted)
        {
            try
            {
                await _schedules.SaveAsync(byId.Values);
            }
            catch
            {
                // Adoption is still applied in memory even if rewriting the file fails.
            }
        }

        foreach (var (session, state) in restored)
        {
            if (IntervalsByName.ContainsKey(state.IntervalName))
                session.SelectedInterval = state.IntervalName;
            session.LastAppliedId = state.LastAppliedId;
            session.ApplyRestoredPausePolicy(state.PauseWhenFullscreen);

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

            session.UserPaused = state.UserPaused;
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

    internal async Task RefreshDisplaysAsync()
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
                await StopSessionAsync(removed);
                Schedules.Remove(removed);
                _sessions.Remove(removed.Monitor.Id);
            }

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
            }

            SetDisplayStatus(monitors.Count == 0
                ? "Windows did not report any connected displays."
                : $"{monitors.Count} display(s) detected. Start a schedule on each display you want Oriole to manage.");
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

            var monitor = session.Monitor;
            var lastId = session.LastAppliedId;
            if (lastId is null && !string.IsNullOrWhiteSpace(monitor.CurrentWallpaper))
            {
                lastId = available.FirstOrDefault(entry =>
                    PathsEqual(entry.LocalPath, monitor.CurrentWallpaper))?.Id;
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
                NotifyTray("Oriole · auto-fetch", message);
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
            NotifyTray("Oriole · auto-fetch", message, error: true);
        }
        catch (Exception ex)
        {
            // A network/download failure is reported but must never kill the loop.
            _feedConsecutiveFailures++;
            var message = $"Auto-fetch could not add wallpapers: {ex.Message}";
            SetFeedStatus(message);
            NotifyTray("Oriole · auto-fetch", message, error: true);
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

    internal async Task StopSessionAsync(DisplayScheduleViewModel session)
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
            session.Countdown = session.IsStarting
                ? "Preparing…"
                : session.ManualRotationRunning || session.PreviousRotationRunning
                    ? "Applying…" : "Not running";
        }
        else if (session.UserPaused || session.FullscreenPaused)
        {
            session.Countdown = "Paused";
        }
        else
        {
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
                ? $"Oriole — next change {deadline.ToLocalTime():HH:mm}"
                : "Oriole — wallpaper manager";
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
}

/// <summary>Per-display row rendered by the AutomationView DataTemplate.</summary>
public sealed class DisplayScheduleViewModel : ViewModelBase
{
    private readonly AutomationViewModel _owner;

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
