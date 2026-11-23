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
        // NavigationService caches views: Unloaded fires when the user leaves this
        // page and Loaded fires again on return. Subscribe per load so "Choose
        // image" keeps working after navigating away and back.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ChooseImageRequested -= OnChooseImageRequested; // no duplicate hooks
        _viewModel.ChooseImageRequested += OnChooseImageRequested;
        // Views are cached by NavigationService, so the constructor's one-time
        // detection is stale on a return visit. Re-read monitors on every load,
        // the same way the Library and Discover pages refresh.
        _viewModel.RefreshCommand.Execute(null);
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
    }
}
