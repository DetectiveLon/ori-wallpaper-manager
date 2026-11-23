using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Oriole.Core.Services;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.Pages;

/// <summary>
/// App-lifetime rotation controls. Wallpapers are selected from the local library so
/// scheduled changes never trigger surprise network downloads.
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

    private readonly RotationLoop _rotationLoop = new();
    private readonly WallpaperLibraryStore _library = new();
    private readonly ComboBox _intervalPicker = new() { MinWidth = 160, Height = 34 };
    private readonly ComboBox _displayPicker = new() { MinWidth = 220, Height = 34 };
    private readonly Button _startButton = MakeButton("Start rotation");
    private readonly Button _pauseButton = MakeButton("Pause");
    private readonly Button _rotateNowButton = MakeButton("Rotate now");
    private readonly Button _stopButton = MakeButton("Stop");
    private readonly TextBlock _countdown = new()
    {
        Text = "Not running",
        FontSize = 27,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White
    };
    private readonly TextBlock _status = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly DispatcherTimer _countdownTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly Dictionary<string, string> _lastAppliedByDisplay = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loopCancellation;
    private Task? _rotationTask;
    private DateTimeOffset? _nextRunUtc;
    private volatile bool _paused;
    private bool _isStarting;
    private bool _manualRotationRunning;
    private bool _shutdownRequested;
    private int _manualRotationGeneration;
    private CancellationTokenSource? _manualRotationCancellation;
    private readonly SemaphoreSlim _rotationGate = new(1, 1);

    public AutomationPage()
    {
        foreach (var interval in Intervals.Keys)
            _intervalPicker.Items.Add(interval);
        _intervalPicker.SelectedItem = "30 minutes";

        LoadDisplays();
        Content = BuildLayout();

        _startButton.Click += async (_, _) => await StartRotationAsync();
        _pauseButton.Click += (_, _) => TogglePause();
        _rotateNowButton.Click += async (_, _) => await RotateNowAsync();
        _stopButton.Click += async (_, _) => await StopRotationAsync();
        _countdownTimer.Tick += (_, _) => UpdateCountdown();

        Loaded += async (_, _) => await RefreshLibraryStatusAsync();
        UpdateButtons();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();

        var scheduleCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 57)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 64, 83)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var schedule = new StackPanel();
        schedule.Children.Add(new TextBlock
        {
            Text = "WALLPAPER ROTATION",
            Foreground = new SolidColorBrush(Color.FromRgb(151, 190, 244)),
            FontWeight = FontWeights.Bold,
            FontSize = 11
        });
        schedule.Children.Add(new TextBlock
        {
            Text = "Change wallpapers on a predictable schedule.",
            Foreground = Brushes.White,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 6)
        });
        schedule.Children.Add(new TextBlock
        {
            Text = "Rotation uses images already saved in your local library. It does not download anything in the background.",
            Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var settings = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
        settings.Children.Add(Labeled("Change every", _intervalPicker));
        settings.Children.Add(Labeled("Target display", _displayPicker));
        schedule.Children.Add(settings);

        var actions = new WrapPanel();
        actions.Children.Add(_startButton);
        actions.Children.Add(_pauseButton);
        actions.Children.Add(_rotateNowButton);
        actions.Children.Add(_stopButton);
        schedule.Children.Add(actions);
        scheduleCard.Child = schedule;
        root.Children.Add(scheduleCard);

        var countdownCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 34, 47)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22)
        };
        var countdownPanel = new StackPanel();
        countdownPanel.Children.Add(new TextBlock
        {
            Text = "NEXT CHANGE",
            Foreground = new SolidColorBrush(Color.FromRgb(151, 190, 244)),
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 9)
        });
        countdownPanel.Children.Add(_countdown);
        countdownPanel.Children.Add(_status);
        countdownPanel.Children.Add(new TextBlock
        {
            Text = "Pause while gaming when you need a stable desktop. If a scheduled change becomes due while paused, the full interval starts again after resume.",
            Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 181)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });
        countdownCard.Child = countdownPanel;
        root.Children.Add(countdownCard);

        return root;
    }

    public Task StopAsync() => StopRotationAsync();

    public void RequestStop()
    {
        _shutdownRequested = true;
        _paused = false;
        _countdownTimer.Stop();
        _nextRunUtc = null;
        _loopCancellation?.Cancel();
        _manualRotationCancellation?.Cancel();
    }

    private async Task StartRotationAsync()
    {
        if (_isStarting || _rotationTask is { IsCompleted: false })
            return;

        if (_intervalPicker.SelectedItem is not string intervalName
            || !Intervals.TryGetValue(intervalName, out var interval))
        {
            _status.Text = "Choose a valid rotation interval.";
            return;
        }

        if (_displayPicker.SelectedItem is not DisplayMonitor)
        {
            _status.Text = "No display is selected. Check the Displays page first.";
            return;
        }

        _isStarting = true;
        UpdateButtons();
        try
        {
            var entries = await _library.GetAllAsync();
            if (_shutdownRequested)
                return;

            var available = entries.Where(entry => File.Exists(entry.LocalPath)).ToArray();
            if (available.Length == 0)
            {
                _status.Text = "Your local library has no available images. Save wallpapers from Discover first.";
                return;
            }

            _loopCancellation = new CancellationTokenSource();
            _paused = false;
            _nextRunUtc = DateTimeOffset.UtcNow + interval;
            _countdownTimer.Start();
            _status.Text = $"{available.Length} local wallpaper(s) available. Rotation is running for the selected display.";

            var token = _loopCancellation.Token;
            _rotationTask = _rotationLoop.RunAsync(
                interval,
                shouldPause: () => _paused,
                rotateAsync: cancellationToken => RotateFromLibraryAsync(
                    cancellationToken,
                    respectPause: true,
                    expectedManualRotationGeneration: Volatile.Read(ref _manualRotationGeneration)),
                nextRunChanged: async nextRun =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _nextRunUtc = nextRun;
                        UpdateCountdown();
                    });
                },
                failed: async exception =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _status.Text = $"Rotation failed: {exception.Message}";
                    });
                },
                cancellationToken: token);

            UpdateButtons();
            _ = ObserveLoopAsync(_rotationTask);
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start rotation: {ex.Message}";
            await StopRotationAsync();
        }
        finally
        {
            _isStarting = false;
            UpdateButtons();
        }
    }

    private async Task ObserveLoopAsync(Task task)
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
            await Dispatcher.InvokeAsync(() => _status.Text = $"Rotation stopped: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_rotationTask, task))
            {
                _rotationTask = null;
                _loopCancellation?.Dispose();
                _loopCancellation = null;
                _countdownTimer.Stop();
                _nextRunUtc = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateCountdown();
                    UpdateButtons();
                });
            }
        }
    }

    private void TogglePause()
    {
        if (_rotationTask is not { IsCompleted: false })
            return;

        _paused = !_paused;
        _status.Text = _paused ? "Rotation paused." : "Rotation resumed.";
        UpdateCountdown();
        UpdateButtons();
    }

    private async Task RotateNowAsync()
    {
        if (_manualRotationRunning || _isStarting || _shutdownRequested)
            return;

        _manualRotationRunning = true;
        var loopToken = _loopCancellation?.Token ?? CancellationToken.None;
        var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        _manualRotationCancellation = manualCancellation;
        UpdateButtons();
        try
        {
            await RotateFromLibraryAsync(manualCancellation.Token, respectPause: false);
            Interlocked.Increment(ref _manualRotationGeneration);
            if (_rotationTask is { IsCompleted: false })
                _rotationLoop.ResetNextRun();
        }
        catch (OperationCanceledException) when (manualCancellation.IsCancellationRequested)
        {
            _status.Text = "Manual rotation was cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not rotate now: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_manualRotationCancellation, manualCancellation))
                _manualRotationCancellation = null;
            manualCancellation.Dispose();
            _manualRotationRunning = false;
            UpdateButtons();
        }
    }

    private async Task RotateFromLibraryAsync(
        CancellationToken cancellationToken,
        bool respectPause,
        int? expectedManualRotationGeneration = null)
    {
        await _rotationGate.WaitAsync(cancellationToken);
        try
        {
            if (expectedManualRotationGeneration is int expectedBeforeLoad
                && expectedBeforeLoad != Volatile.Read(ref _manualRotationGeneration))
            {
                return;
            }

            var entries = await _library.GetAllAsync(cancellationToken);
            var available = entries.Where(entry => File.Exists(entry.LocalPath)).ToArray();
            if (available.Length == 0)
                throw new InvalidOperationException("No available images are saved in the local library.");

            var monitor = await Dispatcher.InvokeAsync(() => _displayPicker.SelectedItem as DisplayMonitor);
            if (monitor is null)
                throw new InvalidOperationException("No display is selected.");

            var lastId = await Dispatcher.InvokeAsync(() =>
            {
                var previousId = _lastAppliedByDisplay.GetValueOrDefault(monitor.Id);
                if (previousId is not null)
                    return previousId;
                if (string.IsNullOrWhiteSpace(monitor.CurrentWallpaper))
                    return null;

                var currentPath = Path.GetFullPath(monitor.CurrentWallpaper);
                return available.FirstOrDefault(entry =>
                    string.Equals(
                        Path.GetFullPath(entry.LocalPath),
                        currentPath,
                        StringComparison.OrdinalIgnoreCase))?.Id;
            });
            var candidates = available.Length > 1
                ? available.Where(entry => !string.Equals(entry.Id, lastId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : available;
            var selected = candidates[Random.Shared.Next(candidates.Length)];

            await Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expectedManualRotationGeneration is int expectedBeforeApply
                    && expectedBeforeApply != Volatile.Read(ref _manualRotationGeneration))
                {
                    _status.Text = "Scheduled change skipped because a manual rotation just completed.";
                    return;
                }

                // Recheck after library lookup and immediately before the Windows apply call.
                if (respectPause && _paused)
                {
                    _status.Text = "Scheduled change skipped because rotation was paused.";
                    return;
                }

                if (!File.Exists(selected.LocalPath))
                    throw new FileNotFoundException("The selected library image is missing.", selected.LocalPath);

                WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, selected.LocalPath);
                _lastAppliedByDisplay[monitor.Id] = selected.Id;
                _status.Text = $"Applied {selected.Title} to {monitor.DeviceName}.";
            });
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    private async Task StopRotationAsync()
    {
        var cancellation = _loopCancellation;
        var task = _rotationTask;

        _manualRotationCancellation?.Cancel();
        _paused = false;
        _countdownTimer.Stop();
        _nextRunUtc = null;
        if (cancellation is not null)
            cancellation.Cancel();

        if (task is not null)
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
                _status.Text = $"Rotation stopped: {ex.Message}";
            }
        }

        if (ReferenceEquals(_loopCancellation, cancellation))
        {
            _loopCancellation?.Dispose();
            _loopCancellation = null;
            _rotationTask = null;
        }

        _countdown.Text = "Not running";
        UpdateButtons();
    }

    private void UpdateCountdown()
    {
        if (_rotationTask is not { IsCompleted: false } || _nextRunUtc is null)
        {
            _countdown.Text = "Not running";
            return;
        }

        if (_paused)
        {
            _countdown.Text = "Paused";
            return;
        }

        var remaining = _nextRunUtc.Value - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        var days = (int)remaining.TotalDays;
        _countdown.Text = days > 0
            ? $"{days}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private async Task RefreshLibraryStatusAsync()
    {
        if (_rotationTask is { IsCompleted: false })
            return;

        try
        {
            var entries = await _library.GetAllAsync();
            _status.Text = entries.Count == 0
                ? "Library empty. Discover and save a few wallpapers before starting rotation."
                : $"{entries.Count} wallpaper(s) saved in your local library.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read the library: {ex.Message}";
        }
    }

    private void LoadDisplays()
    {
        try
        {
            var monitors = WindowsDesktopWallpaper.GetMonitors();
            foreach (var monitor in monitors)
                _displayPicker.Items.Add(monitor);
            _displayPicker.DisplayMemberPath = nameof(DisplayMonitor.DeviceName);
            _displayPicker.SelectedItem = monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? monitors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _status.Text = $"Display detection unavailable: {ex.Message}";
        }
    }

    private void UpdateButtons()
    {
        var running = _rotationTask is { IsCompleted: false };
        var busy = running || _isStarting || _manualRotationRunning;
        _startButton.IsEnabled = !busy;
        _pauseButton.IsEnabled = running;
        _pauseButton.Content = _paused ? "Resume" : "Pause";
        _rotateNowButton.IsEnabled = !_isStarting && !_manualRotationRunning && !_shutdownRequested;
        _stopButton.IsEnabled = running;
        _intervalPicker.IsEnabled = !busy;
        _displayPicker.IsEnabled = !busy;
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(174, 188, 208)),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(control);
        return panel;
    }

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(12, 8, 12, 8),
        Margin = new Thickness(0, 0, 7, 7),
        Background = new SolidColorBrush(Color.FromRgb(69, 104, 151)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(83, 123, 176)),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };
}
