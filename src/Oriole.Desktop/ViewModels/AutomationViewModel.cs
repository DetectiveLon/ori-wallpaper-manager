using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Oriole.Core.Services;
using Oriole.Desktop.Mvvm;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.ViewModels;

/// <summary>
/// Independent app-session wallpaper rotation schedules for each connected display.
/// Scheduled changes use only files already stored in the local library. All
/// persistence and timing mechanics are delegated to the unchanged Core services.
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
            ["1 day"] = TimeSpan.FromDays(1)
        };

    internal static readonly IReadOnlyList<string> IntervalNames = IntervalsByName.Keys.ToArray();

    // Bounded so a stuck COM call cannot prevent MainWindow from closing.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly WallpaperLibraryStore _library = new();
    private readonly ScheduleStore _schedules = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly Dictionary<string, DisplayScheduleViewModel> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _pauseWhenFullscreen = true;
    private bool _shutdownRequested;
    private bool _libraryHasImages;
    private bool _initialized;
    private string _globalStatus = "Preparing…";

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
        RefreshDisplaysCommand = new RelayCommand(_ => { _ = RefreshDisplaysAsync(); });
    }

    /// <summary>One row per connected display, rendered by the AutomationView DataTemplate.</summary>
    public ObservableCollection<DisplayScheduleViewModel> Schedules { get; } = new();

    public string GlobalStatus
    {
        get => _globalStatus;
        private set => SetProperty(ref _globalStatus, value, nameof(GlobalStatus));
    }

    /// <summary>Mirrors the full-screen pause checkbox; read by the rotation loops off-thread.</summary>
    public bool PauseWhenFullscreen
    {
        get => Volatile.Read(ref _pauseWhenFullscreen);
        set
        {
            if (SetProperty(ref _pauseWhenFullscreen, value, nameof(PauseWhenFullscreen)))
                UpdateAllSessions();
        }
    }

    public ICommand RefreshDisplaysCommand { get; }

    /// <summary>Called once from the view's Loaded handler.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized || _shutdownRequested)
            return;
        _initialized = true;

        await RefreshLibraryStatusAsync();
        await RefreshDisplaysAsync();
        await RestoreSchedulesAsync();
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
                    current.LastAppliedId)).ToArray();
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

        IReadOnlyDictionary<string, DisplayScheduleState> saved;
        try
        {
            saved = await _schedules.LoadAsync();
        }
        catch
        {
            return;
        }

        foreach (var session in _sessions.Values)
        {
            if (!saved.TryGetValue(session.Monitor.Id, out var state))
                continue;

            if (IntervalsByName.ContainsKey(state.IntervalName))
                session.SelectedInterval = state.IntervalName;
            session.LastAppliedId = state.LastAppliedId;
            if (!state.ShouldRun)
                continue;

            if (!_libraryHasImages)
            {
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
            GlobalStatus = entries.Count == 0
                ? "Your local library is empty. Save wallpapers from Discover before starting a schedule."
                : $"{entries.Count} wallpaper(s) are saved locally and available for offline rotation.";
        }
        catch (Exception ex)
        {
            _libraryHasImages = false;
            GlobalStatus = $"Could not read the local library: {ex.Message}";
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
                GlobalStatus = $"Could not refresh displays: {detection.Error}";
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

            GlobalStatus = monitors.Count == 0
                ? "Windows did not report any connected displays."
                : $"{monitors.Count} display(s) detected. Start a schedule on each display you want Oriole to manage.";
        }
        catch (Exception ex)
        {
            GlobalStatus = $"Could not refresh displays: {ex.Message}";
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
            && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor));

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

            var entries = await _library.GetAllAsync(cancellationToken);
            var available = entries.Where(entry => File.Exists(entry.LocalPath)).ToArray();
            if (available.Length == 0)
                throw new InvalidOperationException("No available images are saved in the local library.");

            var monitor = session.Monitor;
            var lastId = session.LastAppliedId;
            if (lastId is null && !string.IsNullOrWhiteSpace(monitor.CurrentWallpaper))
            {
                var currentPath = Path.GetFullPath(monitor.CurrentWallpaper);
                lastId = available.FirstOrDefault(entry =>
                    string.Equals(
                        Path.GetFullPath(entry.LocalPath),
                        currentPath,
                        StringComparison.OrdinalIgnoreCase))?.Id;
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
        session.FullscreenPaused = running
            && Volatile.Read(ref _pauseWhenFullscreen)
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
                : session.ManualRotationRunning ? "Applying…" : "Not running";
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

        session.StatusText = session.UserPaused
            ? "Paused manually. Resume to restart the full interval."
            : session.FullscreenPaused
                ? "Paused: a full-screen foreground window is active on this display. The full interval restarts when it closes."
                : session.StatusMessage;

        var busy = running || session.IsStarting || session.ManualRotationRunning;
        session.CanStart = !busy && !_shutdownRequested && _libraryHasImages;
        session.CanPause = running;
        session.PauseButtonText = session.UserPaused ? "Resume" : "Pause";
        session.CanRotateNow = !session.IsStarting
            && !session.ManualRotationRunning
            && !_shutdownRequested
            && _libraryHasImages;
        session.CanStop = running || session.IsStarting || session.ManualRotationRunning;
        session.IntervalEnabled = !busy;
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
    private string _statusText = "Ready. Start this display's schedule when you are.";
    private string _pauseButtonText = "Pause";
    private bool _canStart;
    private bool _canPause;
    private bool _canRotateNow;
    private bool _canStop;
    private bool _intervalEnabled = true;

    public DisplayScheduleViewModel(DisplayMonitor monitor, AutomationViewModel owner)
    {
        _owner = owner;
        Monitor = monitor;
        Intervals = AutomationViewModel.IntervalNames;

        StartCommand = new RelayCommand(_ => { _ = _owner.StartRotationAsync(this); });
        PauseCommand = new RelayCommand(_ => _owner.TogglePause(this));
        RotateNowCommand = new RelayCommand(_ => { _ = _owner.RotateNowAsync(this); });
        StopCommand = new RelayCommand(_ => { _ = _owner.StopSessionAsync(this); });
    }

    public IReadOnlyList<string> Intervals { get; }

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
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
    internal int ManualRotationGeneration;
    internal bool ActiveSchedule;
    internal string? LastAppliedId;
    internal string StatusMessage = "Ready. Start this display's schedule when you are.";
}
