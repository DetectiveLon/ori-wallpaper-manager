using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Oriole.Core.Services;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.Pages;

/// <summary>
/// Independent app-session wallpaper rotation schedules for each connected display.
/// Scheduled changes use only files already stored in the local library.
/// </summary>
public sealed class AutomationPage : UserControl
{
    private static readonly IReadOnlyDictionary<string, TimeSpan> Intervals =
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

    private readonly WallpaperLibraryStore _library = new();
    private readonly StackPanel _sessionCards = new();
    private readonly TextBlock _globalStatus = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 12, 2, 0)
    };
    private readonly CheckBox _pauseWhenFullscreenToggle = new()
    {
        Content = "Pause a display's schedule while a full-screen app is active on it",
        IsChecked = true,
        Foreground = new SolidColorBrush(Color.FromRgb(205, 216, 230)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 16, 0)
    };
    private readonly Button _refreshDisplaysButton = MakeButton("Refresh displays");
    private readonly DispatcherTimer _uiTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly Dictionary<string, RotationSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _rotationGate = new(1, 1);

    private volatile bool _pauseWhenFullscreen = true;
    private bool _shutdownRequested;

    public AutomationPage()
    {
        Content = BuildLayout();

        _pauseWhenFullscreenToggle.Checked += (_, _) =>
        {
            _pauseWhenFullscreen = true;
            UpdateAllSessions();
        };
        _pauseWhenFullscreenToggle.Unchecked += (_, _) =>
        {
            _pauseWhenFullscreen = false;
            UpdateAllSessions();
        };
        _refreshDisplaysButton.Click += async (_, _) => await RefreshDisplaysAsync();
        _uiTimer.Tick += (_, _) => UpdateAllSessions();

        Loaded += async (_, _) =>
        {
            await RefreshDisplaysAsync();
            await RefreshLibraryStatusAsync();
            _uiTimer.Start();
        };

        _ = RefreshDisplaysAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 57)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 64, 83)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var headerContent = new StackPanel();
        headerContent.Children.Add(new TextBlock
        {
            Text = "WALLPAPER AUTOMATION",
            Foreground = new SolidColorBrush(Color.FromRgb(151, 190, 244)),
            FontWeight = FontWeights.Bold,
            FontSize = 11
        });
        headerContent.Children.Add(new TextBlock
        {
            Text = "A separate schedule for every screen.",
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 6)
        });
        headerContent.Children.Add(new TextBlock
        {
            Text = "Each display has its own interval and controls. Rotation uses wallpapers already saved in your local library, so scheduled changes never trigger background downloads.",
            Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 15)
        });

        var options = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        options.Children.Add(_pauseWhenFullscreenToggle);
        options.Children.Add(_refreshDisplaysButton);
        headerContent.Children.Add(options);
        headerContent.Children.Add(new TextBlock
        {
            Text = "Full-screen detection is based on the foreground window covering a display. It pauses for full-screen apps such as games and video players; it does not try to identify games by name.",
            Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 181)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });
        header.Child = headerContent;
        root.Children.Add(header);

        root.Children.Add(_sessionCards);
        root.Children.Add(_globalStatus);
        return root;
    }

    public Task StopAsync() => Task.WhenAll(_sessions.Values.Select(StopSessionAsync));

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

    private async Task RefreshDisplaysAsync()
    {
        if (_shutdownRequested)
            return;

        try
        {
            var monitors = WindowsDesktopWallpaper.GetMonitors();
            var connectedIds = monitors
                .Select(monitor => monitor.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var removed in _sessions.Values
                         .Where(session => !connectedIds.Contains(session.Monitor.Id))
                         .ToArray())
            {
                await StopSessionAsync(removed);
                _sessionCards.Children.Remove(removed.Card);
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

                var session = new RotationSession(monitor);
                session.Card = BuildSessionCard(session);
                _sessions.Add(monitor.Id, session);
                _sessionCards.Children.Add(session.Card);
                UpdateSessionUi(session);
            }

            _globalStatus.Text = monitors.Count == 0
                ? "Windows did not report any connected displays."
                : $"{monitors.Count} display(s) detected. Start a schedule on each display you want Oriole to manage.";
        }
        catch (Exception ex)
        {
            _globalStatus.Text = $"Could not refresh displays: {ex.Message}";
        }
    }

    private Border BuildSessionCard(RotationSession session)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 34, 47)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 64, 83)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var content = new StackPanel();
        var displayHeading = new StackPanel();
        displayHeading.Children.Add(new TextBlock
        {
            Text = session.Monitor.IsPrimary
                ? $"{session.Monitor.DeviceName}  ·  PRIMARY"
                : session.Monitor.DeviceName,
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        displayHeading.Children.Add(new TextBlock
        {
            Text = $"{session.Monitor.Width:N0} × {session.Monitor.Height:N0}  ·  Position {session.Monitor.Left}, {session.Monitor.Top}",
            Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 181)),
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 14)
        });
        content.Children.Add(displayHeading);

        content.Children.Add(new TextBlock
        {
            Text = "NEXT CHANGE",
            Foreground = new SolidColorBrush(Color.FromRgb(151, 190, 244)),
            FontWeight = FontWeights.Bold,
            FontSize = 10
        });
        content.Children.Add(session.Countdown);
        content.Children.Add(session.Status);

        var controls = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var intervalPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 7) };
        intervalPanel.Children.Add(new TextBlock
        {
            Text = "Change every",
            Foreground = new SolidColorBrush(Color.FromRgb(174, 188, 208)),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 4)
        });
        foreach (var interval in Intervals.Keys)
            session.IntervalPicker.Items.Add(interval);
        session.IntervalPicker.SelectedItem = "30 minutes";
        intervalPanel.Children.Add(session.IntervalPicker);

        controls.Children.Add(intervalPanel);
        controls.Children.Add(session.StartButton);
        controls.Children.Add(session.PauseButton);
        controls.Children.Add(session.RotateNowButton);
        controls.Children.Add(session.StopButton);
        content.Children.Add(controls);
        card.Child = content;

        session.StartButton.Click += async (_, _) => await StartRotationAsync(session);
        session.PauseButton.Click += (_, _) => TogglePause(session);
        session.RotateNowButton.Click += async (_, _) => await RotateNowAsync(session);
        session.StopButton.Click += async (_, _) => await StopSessionAsync(session);

        return card;
    }

    private async Task StartRotationAsync(RotationSession session)
    {
        if (_shutdownRequested || session.IsStarting || IsRunning(session))
            return;

        if (session.IntervalPicker.SelectedItem is not string intervalName
            || !Intervals.TryGetValue(intervalName, out var interval))
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
            session.UserPaused = false;
            session.NextRunUtc = DateTimeOffset.UtcNow + interval;
            session.StatusMessage = $"{available.Length} local wallpaper(s) available. This display's schedule is running.";

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
                    await Dispatcher.InvokeAsync(() =>
                    {
                        session.NextRunUtc = nextRun;
                        UpdateSessionUi(session);
                    });
                },
                failed: async exception =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        session.StatusMessage = $"Rotation failed: {exception.Message}";
                        UpdateSessionUi(session);
                    });
                },
                cancellationToken: token);

            session.LoopTask = task;
            _ = ObserveLoopAsync(session, task);
        }
        catch (Exception ex)
        {
            session.StatusMessage = $"Could not start rotation: {ex.Message}";
            await StopSessionAsync(session);
        }
        finally
        {
            session.IsStarting = false;
            UpdateSessionUi(session);
        }
    }

    private async Task ObserveLoopAsync(RotationSession session, Task task)
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
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(session.LoopTask, task))
                    FinishLoop(session, task);
            });
        }
    }

    private void FinishLoop(RotationSession session, Task task)
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

    private bool ShouldPause(RotationSession session)
    {
        return session.UserPaused
            || (_pauseWhenFullscreen && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor));
    }

    private void TogglePause(RotationSession session)
    {
        if (!IsRunning(session))
            return;

        session.UserPaused = !session.UserPaused;
        session.StatusMessage = session.UserPaused ? "Paused manually." : "Manual pause released.";
        UpdateSessionUi(session);
    }

    private async Task RotateNowAsync(RotationSession session)
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
        RotationSession session,
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

            await Dispatcher.InvokeAsync(() =>
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
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    private async Task StopSessionAsync(RotationSession session)
    {
        var loopCancellation = session.LoopCancellation;
        var loopTask = session.LoopTask;
        var manualTask = session.ManualTask;

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
    }

    private void UpdateAllSessions()
    {
        foreach (var session in _sessions.Values)
            UpdateSessionUi(session);
    }

    private void UpdateSessionUi(RotationSession session)
    {
        var running = IsRunning(session);
        session.FullscreenPaused = running
            && _pauseWhenFullscreen
            && FullscreenWindowDetector.IsFullscreenOnDisplay(session.Monitor);

        if (!running || session.NextRunUtc is null)
        {
            session.Countdown.Text = session.IsStarting
                ? "Preparing…"
                : session.ManualRotationRunning ? "Applying…" : "Not running";
        }
        else if (session.UserPaused || session.FullscreenPaused)
        {
            session.Countdown.Text = "Paused";
        }
        else
        {
            var remaining = session.NextRunUtc.Value - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            var days = (int)remaining.TotalDays;
            session.Countdown.Text = days > 0
                ? $"{days}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        session.Status.Text = session.UserPaused
            ? "Paused manually. Resume to restart the full interval."
            : session.FullscreenPaused
                ? "Paused: a full-screen foreground window is active on this display. The full interval restarts when it closes."
                : session.StatusMessage;

        var busy = running || session.IsStarting || session.ManualRotationRunning;
        session.StartButton.IsEnabled = !busy && !_shutdownRequested;
        session.PauseButton.IsEnabled = running;
        session.PauseButton.Content = session.UserPaused ? "Resume" : "Pause";
        session.RotateNowButton.IsEnabled = !session.IsStarting
            && !session.ManualRotationRunning
            && !_shutdownRequested;
        session.StopButton.IsEnabled = running || session.IsStarting || session.ManualRotationRunning;
        session.IntervalPicker.IsEnabled = !busy;
    }

    private async Task RefreshLibraryStatusAsync()
    {
        if (_shutdownRequested)
            return;

        try
        {
            var entries = await _library.GetAllAsync();
            _globalStatus.Text = entries.Count == 0
                ? "Your local library is empty. Save wallpapers from Discover before starting a schedule."
                : $"{entries.Count} wallpaper(s) are saved locally and available for offline rotation.";
        }
        catch (Exception ex)
        {
            _globalStatus.Text = $"Could not read the local library: {ex.Message}";
        }
    }

    private static bool IsRunning(RotationSession session) =>
        session.LoopTask is { IsCompleted: false };

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(14, 9, 14, 9),
        Margin = new Thickness(0, 0, 8, 7),
        Background = new SolidColorBrush(Color.FromRgb(47, 65, 86)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(65, 86, 111)),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private sealed class RotationSession(DisplayMonitor monitor)
    {
        public volatile DisplayMonitor Monitor = monitor;
        public readonly ComboBox IntervalPicker = new() { MinWidth = 148, Height = 34 };
        public readonly TextBlock Countdown = new()
        {
            Text = "Not running",
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 7, 0, 3)
        };
        public readonly TextBlock Status = new()
        {
            Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
            TextWrapping = TextWrapping.Wrap
        };
        public readonly Button StartButton = MakeButton("Start");
        public readonly Button PauseButton = MakeButton("Pause");
        public readonly Button RotateNowButton = MakeButton("Rotate now");
        public readonly Button StopButton = MakeButton("Stop");

        public RotationLoop Loop = new();
        public Border Card = new();
        public CancellationTokenSource? LoopCancellation;
        public Task? LoopTask;
        public CancellationTokenSource? ManualCancellation;
        public Task? ManualTask;
        public DateTimeOffset? NextRunUtc;
        public volatile bool UserPaused;
        public bool FullscreenPaused;
        public bool IsStarting;
        public bool ManualRotationRunning;
        public int ManualRotationGeneration;
        public string? LastAppliedId;
        public string StatusMessage = "Ready. Start this display's schedule when you are.";
    }
}
