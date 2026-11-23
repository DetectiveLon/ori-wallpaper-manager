using System.Collections.ObjectModel;
using System.Windows.Input;
using Oriole.Desktop.Mvvm;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.ViewModels;

/// <summary>View model for the Displays page: enumerates monitors and applies per-display wallpapers.</summary>
public sealed class DisplaysViewModel : ViewModelBase
{
    private string _status = string.Empty;

    public DisplaysViewModel()
    {
        RefreshCommand = new RelayCommand(_ => RefreshDisplays());
        ChooseImageCommand = new RelayCommand(parameter =>
        {
            if (parameter is not DisplayCardViewModel monitor)
            {
                Status = "Could not identify the selected display. Use Refresh and try again.";
                return;
            }

            // Never fail silently: if the view is not attached, say so inline.
            if (ChooseImageRequested is null)
            {
                Status = $"The wallpaper picker is unavailable for {monitor.DeviceName}. Re-open Displays and try again.";
                return;
            }

            ChooseImageRequested.Invoke(monitor);
        });

        RefreshDisplays();
    }

    public ObservableCollection<DisplayCardViewModel> Monitors { get; } = new();

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value, nameof(Status));
    }

    public ICommand RefreshCommand { get; }
    public ICommand ChooseImageCommand { get; }

    /// <summary>View service hook: the view opens the file picker for this monitor.</summary>
    public event Action<DisplayCardViewModel>? ChooseImageRequested;

    /// <summary>Applies the picked image to one display and reports the result inline.</summary>
    public void ApplyWallpaper(DisplayCardViewModel monitor, string imagePath)
    {
        try
        {
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, imagePath);
            RefreshDisplays();
            Status = $"Wallpaper applied to {monitor.DeviceName}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not apply wallpaper to {monitor.DeviceName}: {ex.Message}";
        }
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
                return;
            }

            var monitors = detection.Monitors;
            if (monitors.Count == 0)
            {
                Status = "Windows did not report any connected displays.";
                return;
            }

            foreach (var monitor in monitors)
                Monitors.Add(new DisplayCardViewModel(monitor));

            Status = $"{monitors.Count} display(s) detected. Image assignment is independent per display; Windows currently uses one shared fit mode.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read display information: {ex.Message}";
        }
    }
}

/// <summary>Read-only card state for one connected monitor.</summary>
public sealed class DisplayCardViewModel
{
    public DisplayCardViewModel(DisplayMonitor monitor)
    {
        Id = monitor.Id;
        DeviceName = monitor.DeviceName;
        IsPrimary = monitor.IsPrimary;
        Left = monitor.Left;
        Top = monitor.Top;
        Width = monitor.Width;
        Height = monitor.Height;
        CurrentWallpaper = monitor.CurrentWallpaper;
    }

    public string Id { get; }
    public string DeviceName { get; }
    public bool IsPrimary { get; }
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public string? CurrentWallpaper { get; }

    public string ResolutionText => $"{Width:N0} × {Height:N0}  ·  Position {Left}, {Top}";

    public string WallpaperText =>
        CurrentWallpaper ?? "No wallpaper path reported by Windows";
}
