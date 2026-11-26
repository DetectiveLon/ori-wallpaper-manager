using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>View model for the Displays page: enumerates monitors and applies per-display wallpapers.</summary>
public sealed class DisplaysViewModel : ViewModelBase
{
    // Diagram geometry. The canvas fits the virtual desktop into a readable
    // surface, but never shrinks a monitor card below the size its content needs.
    private const double DiagramMaxWidth = 720;
    private const double DiagramMaxHeight = 380;
    private const double DiagramPadding = 16;
    private const double MinimumCardWidth = 260;
    private const double MinimumCardHeight = 190;

    private string _status = string.Empty;
    private DesktopWallpaperPosition _currentPosition = DesktopWallpaperPosition.Fill;
    private WallpaperPositionOption? _selectedPositionOption;
    private bool _suppressPositionApplication;
    private DisplayCardViewModel? _selectedMonitor;
    private double _layoutWidth = DiagramMaxWidth;
    private double _layoutHeight = 220;
    private double _originLeft;
    private double _originTop;
    private bool _originVisible;
    private bool _noMonitors;

    public DisplaysViewModel()
    {
        RefreshCommand = new RelayCommand(_ => RefreshDisplays());
        SelectMonitorCommand = new RelayCommand(parameter =>
        {
            if (parameter is not DisplayCardViewModel monitor)
                return;

            SetSelectedMonitor(monitor, persist: true);
            Status = $"{monitor.DisplayName} is now the target display for Apply actions.";
        });
        ChooseImageCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is not DisplayCardViewModel monitor)
                {
                    Status = "Could not identify the selected display. Use Refresh and try again.";
                    return;
                }

                if (IsSpanActive)
                {
                    Status = "Span mode is active: Windows ignores per-monitor images. Choose a fit mode other than Span first.";
                    return;
                }

                // Remember the target so a page visit cannot silently reset it to primary.
                SetSelectedMonitor(monitor, persist: true);

                // Never fail silently: if the view is not attached, say so inline.
                if (ChooseImageRequested is null)
                {
                    Status = $"The wallpaper picker is unavailable for {monitor.DisplayName}. Re-open Displays and try again.";
                    return;
                }

                ChooseImageRequested.Invoke(monitor);
            },
            _ => !IsSpanActive);

        SetPositionCommand = new RelayCommand(parameter =>
        {
            if (parameter is DesktopWallpaperPosition position)
                ApplyPosition(position);
        });

        SetSpanWallpaperCommand = new RelayCommand(_ =>
        {
            if (ChooseSpanImageRequested is null)
            {
                Status = "The spanning-image picker is unavailable. Re-open Displays and try again.";
                return;
            }

            ChooseSpanImageRequested.Invoke();
        });

        RefreshDisplays();
    }

    public ObservableCollection<DisplayCardViewModel> Monitors { get; } = new();

    /// <summary>The six fit modes Windows supports, in the order shown in the picker.</summary>
    public IReadOnlyList<WallpaperPositionOption> PositionOptions { get; } = new[]
    {
        new WallpaperPositionOption(DesktopWallpaperPosition.Fill, "Fill"),
        new WallpaperPositionOption(DesktopWallpaperPosition.Fit, "Fit"),
        new WallpaperPositionOption(DesktopWallpaperPosition.Stretch, "Stretch"),
        new WallpaperPositionOption(DesktopWallpaperPosition.Center, "Center"),
        new WallpaperPositionOption(DesktopWallpaperPosition.Tile, "Tile"),
        new WallpaperPositionOption(DesktopWallpaperPosition.Span, "Span")
    };

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value, nameof(Status));
    }

    public ICommand RefreshCommand { get; }
    public ICommand SelectMonitorCommand { get; }
    public ICommand ChooseImageCommand { get; }
    public ICommand SetPositionCommand { get; }
    public ICommand SetSpanWallpaperCommand { get; }

    /// <summary>View service hook: the view opens the file picker for this monitor.</summary>
    public event Action<DisplayCardViewModel>? ChooseImageRequested;

    /// <summary>View service hook: the view opens the file picker for the Span image.</summary>
    public event Action? ChooseSpanImageRequested;

    /// <summary>The remembered target display, falling back to primary after a refresh.</summary>
    public DisplayCardViewModel? SelectedMonitor => _selectedMonitor;

    public bool IsSpanActive => CurrentPosition == DesktopWallpaperPosition.Span;

    // Per-mode flags drive the fit illustration's visual states in XAML without a
    // converter or a second page-level style library.
    public bool IsFill => CurrentPosition == DesktopWallpaperPosition.Fill;
    public bool IsFit => CurrentPosition == DesktopWallpaperPosition.Fit;
    public bool IsStretch => CurrentPosition == DesktopWallpaperPosition.Stretch;
    public bool IsCenter => CurrentPosition == DesktopWallpaperPosition.Center;
    public bool IsTile => CurrentPosition == DesktopWallpaperPosition.Tile;
    public bool IsSpan => CurrentPosition == DesktopWallpaperPosition.Span;

    /// <summary>Binding source for disabling per-monitor image actions while Span is active.</summary>
    public bool CanChoosePerMonitorImage => !IsSpanActive;

    // Geometry for the monitor diagram. Values are DIPs inside the Canvas, derived
    // from DisplayMonitor.Left/Top/Width/Height at every refresh.
    public double LayoutWidth
    {
        get => _layoutWidth;
        private set => SetProperty(ref _layoutWidth, value, nameof(LayoutWidth));
    }

    public double LayoutHeight
    {
        get => _layoutHeight;
        private set => SetProperty(ref _layoutHeight, value, nameof(LayoutHeight));
    }

    public double OriginLeft
    {
        get => _originLeft;
        private set => SetProperty(ref _originLeft, value, nameof(OriginLeft));
    }

    public double OriginTop
    {
        get => _originTop;
        private set => SetProperty(ref _originTop, value, nameof(OriginTop));
    }

    public bool OriginVisible
    {
        get => _originVisible;
        private set => SetProperty(ref _originVisible, value, nameof(OriginVisible));
    }

    public bool NoMonitors
    {
        get => _noMonitors;
        private set => SetProperty(ref _noMonitors, value, nameof(NoMonitors));
    }

    public DesktopWallpaperPosition CurrentPosition
    {
        get => _currentPosition;
        private set
        {
            if (!SetProperty(ref _currentPosition, value, nameof(CurrentPosition)))
                return;

            // Update the ComboBox selection without re-entering its setter, which
            // would apply the mode a second time.
            _selectedPositionOption = PositionOptions.FirstOrDefault(option => option.Position == value);
            OnPropertyChanged(nameof(SelectedPositionOption));
            OnPropertyChanged(nameof(PositionDescription));
            OnPropertyChanged(nameof(IsSpanActive));
            OnPropertyChanged(nameof(IsFill));
            OnPropertyChanged(nameof(IsFit));
            OnPropertyChanged(nameof(IsStretch));
            OnPropertyChanged(nameof(IsCenter));
            OnPropertyChanged(nameof(IsTile));
            OnPropertyChanged(nameof(IsSpan));
            OnPropertyChanged(nameof(CanChoosePerMonitorImage));
            ((RelayCommand)ChooseImageCommand).RaiseCanExecuteChanged();
        }
    }

    public WallpaperPositionOption? SelectedPositionOption
    {
        get => _selectedPositionOption;
        set
        {
            if (ReferenceEquals(_selectedPositionOption, value))
                return;

            _selectedPositionOption = value;
            OnPropertyChanged(nameof(SelectedPositionOption));
            if (_suppressPositionApplication || value is null || value.Position == CurrentPosition)
                return;

            if (value.Position == DesktopWallpaperPosition.Span)
            {
                // The view asks for one image and calls ApplySpanWallpaper. If the
                // dialog is cancelled or fails, restore the real OS mode.
                SetSpanWallpaperCommand.Execute(null);
                SyncPositionSelection();
                return;
            }

            SetPositionCommand.Execute(value.Position);
        }
    }

    public string PositionDescription => CurrentPosition switch
    {
        DesktopWallpaperPosition.Fill => "Fill: the image covers each screen; edges may be cropped.",
        DesktopWallpaperPosition.Fit => "Fit: the whole image is visible; bars may appear.",
        DesktopWallpaperPosition.Stretch => "Stretch: the image is stretched to the screen and may look distorted.",
        DesktopWallpaperPosition.Center => "Center: the image is shown at its original size.",
        DesktopWallpaperPosition.Tile => "Tile: the image repeats across the screen.",
        DesktopWallpaperPosition.Span => "Span: one image stretches across every display.",
        _ => "Windows did not report a recognised fit mode; Fill is assumed."
    };

    /// <summary>Applies the picked image to one display and reports the result inline.</summary>
    public void ApplyWallpaper(DisplayCardViewModel monitor, string imagePath)
    {
        if (IsSpanActive)
        {
            Status = "Span mode is active: Windows ignores per-monitor images. Choose another fit mode first.";
            return;
        }

        try
        {
            SetSelectedMonitor(monitor, persist: true);
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, imagePath);
            RefreshDisplays();
            Status = $"Wallpaper applied to {monitor.DisplayName}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not apply wallpaper to {monitor.DisplayName}: {ex.Message}";
        }
    }

    /// <summary>Applies one image across all displays in Span mode.</summary>
    public void ApplySpanWallpaper(string imagePath)
    {
        try
        {
            WindowsDesktopWallpaper.ApplySpanWallpaper(imagePath);
            RefreshDisplays();
            Status = "Span wallpaper applied to every display. Per-monitor images are ignored while Span is active.";
        }
        catch (Exception ex)
        {
            Status = $"Could not apply the spanning wallpaper: {ex.Message}";
            SyncPositionSelection();
        }
    }

    private void ApplyPosition(DesktopWallpaperPosition position)
    {
        try
        {
            WindowsDesktopWallpaper.SetPosition(position);
            CurrentPosition = position;
            Status = $"Fit mode set. {PositionDescription} This applies to every display.";
        }
        catch (Exception ex)
        {
            Status = $"Could not change the fit mode: {ex.Message}";
            SyncPositionSelection();
        }
    }

    private void SyncPositionSelection()
    {
        _selectedPositionOption = PositionOptions.FirstOrDefault(option => option.Position == CurrentPosition);
        OnPropertyChanged(nameof(SelectedPositionOption));
    }

    private void SetSelectedMonitor(DisplayCardViewModel? monitor, bool persist)
    {
        if (_selectedMonitor is not null)
            _selectedMonitor.IsSelected = false;

        _selectedMonitor = monitor;
        if (monitor is not null)
            monitor.IsSelected = true;

        OnPropertyChanged(nameof(SelectedMonitor));
        if (monitor is not null && persist)
            MonitorSelectionService.Instance.Select(monitor.Id);
    }

    private void RefreshDisplays()
    {
        Monitors.Clear();
        try
        {
            var detection = DisplayDetection.Read();
            if (!detection.Succeeded)
            {
                Status = $"Could not read display information: {detection.Error}";
                RefreshPosition();
                return;
            }

            var monitors = detection.Monitors;
            if (monitors.Count == 0)
            {
                Status = "Windows did not report any connected displays.";
                RefreshPosition();
                return;
            }

            for (var index = 0; index < monitors.Count; index++)
                Monitors.Add(new DisplayCardViewModel(monitors[index], index));

            // Resolve the remembered target against the live ids before falling
            // back to primary; this is what stops Apply jumping to the wrong screen.
            MonitorSelectionService.Instance.Load(Monitors.Select(monitor => monitor.Id));
            var primary = Monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? Monitors[0];
            var rememberedId = MonitorSelectionService.Instance.Resolve(primary.Id);
            var target = Monitors.FirstOrDefault(monitor => monitor.Id == rememberedId) ?? primary;
            SetSelectedMonitor(target, persist: false);

            RefreshPosition();
            Status = $"{Monitors.Count} display(s) detected. Images can differ per display; Windows uses one shared fit mode.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read display information: {ex.Message}";
        }
        finally
        {
            // Detection can fail at any point; the diagram must still reflect the
            // monitors that were actually read, even if that list is empty.
            UpdateLayout();
        }
    }

    /// <summary>
    /// Maps the virtual desktop reported by Windows onto the diagram canvas.
    /// Cards keep their relative positions, and never become too small to use.
    /// </summary>
    private void UpdateLayout()
    {
        if (Monitors.Count == 0)
        {
            LayoutWidth = DiagramMaxWidth;
            LayoutHeight = 220;
            OriginVisible = false;
            NoMonitors = true;
            return;
        }

        NoMonitors = false;

        var left = Monitors.Min(monitor => monitor.Left);
        var top = Monitors.Min(monitor => monitor.Top);
        var right = Monitors.Max(monitor => monitor.Left + monitor.Width);
        var bottom = Monitors.Max(monitor => monitor.Top + monitor.Height);
        var virtualWidth = Math.Max(1d, (double)right - left);
        var virtualHeight = Math.Max(1d, (double)bottom - top);

        var fitScale = Math.Min(
            (DiagramMaxWidth - 2 * DiagramPadding) / virtualWidth,
            (DiagramMaxHeight - 2 * DiagramPadding) / virtualHeight);
        var minimumScale = Monitors.Max(monitor =>
            Math.Max(
                MinimumCardWidth / Math.Max(1d, monitor.Width),
                MinimumCardHeight / Math.Max(1d, monitor.Height)));
        var scale = Math.Max(fitScale, minimumScale);

        foreach (var monitor in Monitors)
        {
            monitor.SetDiagramBounds(
                left: DiagramPadding + (monitor.Left - left) * scale,
                top: DiagramPadding + (monitor.Top - top) * scale,
                width: monitor.Width * scale,
                height: monitor.Height * scale);
        }

        LayoutWidth = virtualWidth * scale + 2 * DiagramPadding;
        LayoutHeight = virtualHeight * scale + 2 * DiagramPadding;
        OriginLeft = DiagramPadding - left * scale;
        OriginTop = DiagramPadding - top * scale;
        OriginVisible = left <= 0 && right >= 0 && top <= 0 && bottom >= 0;
    }

    private void RefreshPosition()
    {
        try
        {
            // A programmatic refresh must never be mistaken for a user choosing
            // a fit mode and pushed back to Windows as a change.
            _suppressPositionApplication = true;
            CurrentPosition = WindowsDesktopWallpaper.GetPosition();
        }
        catch (Exception ex)
        {
            Status = $"Could not read the current fit mode: {ex.Message}";
        }
        finally
        {
            _suppressPositionApplication = false;
        }
    }
}

/// <summary>One selectable fit mode in the global fit picker.</summary>
public sealed class WallpaperPositionOption
{
    public WallpaperPositionOption(DesktopWallpaperPosition position, string name)
    {
        Position = position;
        Name = name;
    }

    public DesktopWallpaperPosition Position { get; }
    public string Name { get; }

    public string Label => Position == DesktopWallpaperPosition.Span
        ? "Span - one image across all displays"
        : Name;

    public override string ToString() => Label;
}

/// <summary>Read-only card state for one connected monitor, plus its selection flag.</summary>
public sealed class DisplayCardViewModel : ViewModelBase
{
    private bool _isSelected;
    private double _diagramLeft;
    private double _diagramTop;
    private double _diagramWidth;
    private double _diagramHeight;

    public DisplayCardViewModel(DisplayMonitor monitor, int index = 0)
    {
        Id = monitor.Id;
        DeviceName = monitor.DeviceName;
        DisplayName = $"Display {index + 1}";
        IsPrimary = monitor.IsPrimary;
        Left = monitor.Left;
        Top = monitor.Top;
        Width = monitor.Width;
        Height = monitor.Height;
        CurrentWallpaper = monitor.CurrentWallpaper;
    }

    public string Id { get; }
    public string DeviceName { get; }
    public string DisplayName { get; }
    public bool IsPrimary { get; }
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public string? CurrentWallpaper { get; }

    /// <summary>Canvas bounds for the scaled layout diagram, set by the page view model.</summary>
    public double DiagramLeft => _diagramLeft;
    public double DiagramTop => _diagramTop;
    public double DiagramWidth => _diagramWidth;
    public double DiagramHeight => _diagramHeight;

    internal void SetDiagramBounds(double left, double top, double width, double height)
    {
        SetProperty(ref _diagramLeft, left, nameof(DiagramLeft));
        SetProperty(ref _diagramTop, top, nameof(DiagramTop));
        SetProperty(ref _diagramWidth, width, nameof(DiagramWidth));
        SetProperty(ref _diagramHeight, height, nameof(DiagramHeight));
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public string ResolutionText => $"{Width:N0} x {Height:N0}  |  Position {Left}, {Top}";

    /// <summary>The file name only: raw COM paths are long and not useful on a card.</summary>
    public string WallpaperName => string.IsNullOrWhiteSpace(CurrentWallpaper)
        ? "No wallpaper reported by Windows"
        : Path.GetFileName(CurrentWallpaper.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Kept for existing bindings; cards show WallpaperName instead of the raw path.</summary>
    public string WallpaperText => WallpaperName;

    public string WallpaperPathToolTip => CurrentWallpaper ?? "Windows did not report a wallpaper path for this display.";

    public string ChooseImageAutomationName => $"Choose wallpaper for {DisplayName} ({DeviceName})";
}
