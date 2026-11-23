using Microsoft.Win32;
using Oriole.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Oriole.Desktop.Views;

/// <summary>XAML view for the Displays page. Code-behind only hosts the native file picker.</summary>
public partial class DisplaysView : UserControl
{
    private readonly DisplaysViewModel _viewModel;

    public DisplaysView()
    {
        InitializeComponent();
        _viewModel = new DisplaysViewModel();
        DataContext = _viewModel;
        _viewModel.ChooseImageRequested += OnChooseImageRequested;
        Unloaded += OnUnloaded;
    }

    private void OnChooseImageRequested(DisplayCardViewModel monitor)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose wallpaper for {monitor.DeviceName}",
            Filter = "Wallpaper images|*.jpg;*.jpeg;*.png;*.bmp;*.dib;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var owner = Window.GetWindow(this);
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != true)
            return;

        // Errors are reported inline by the view model; no modal error dialogs.
        _viewModel.ApplyWallpaper(monitor, dialog.FileName);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ChooseImageRequested -= OnChooseImageRequested;
        Unloaded -= OnUnloaded;
    }
}
