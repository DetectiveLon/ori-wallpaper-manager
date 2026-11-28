using Microsoft.Win32;
using Ori.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Ori.Desktop.Views;

/// <summary>XAML view for the Displays page. Code-behind hosts pickers and control events only.</summary>
public partial class DisplaysView : UserControl
{
    private readonly DisplaysViewModel _viewModel;

    public DisplaysView()
    {
        InitializeComponent();
        _viewModel = new DisplaysViewModel();
        DataContext = _viewModel;
        // NavigationService caches views: Unloaded fires when the user leaves this
        // page and Loaded fires again on return. Subscribe per load so the file
        // pickers keep working after navigating away and back.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ChooseImageRequested -= OnChooseImageRequested; // no duplicate hooks
        _viewModel.ChooseImageRequested += OnChooseImageRequested;
        _viewModel.ChooseSpanImageRequested -= OnChooseSpanImageRequested;
        _viewModel.ChooseSpanImageRequested += OnChooseSpanImageRequested;
        // Views are cached by NavigationService, so the constructor's one-time
        // detection is stale on a return visit. Re-read monitors every load and
        // rebuild the library/feed candidates without applying anything.
        _viewModel.RefreshCommand.Execute(null);
        _ = _viewModel.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ChooseImageRequested -= OnChooseImageRequested;
        _viewModel.ChooseSpanImageRequested -= OnChooseSpanImageRequested;
    }

    private void OnSourceKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<SourceKindOption>().FirstOrDefault() is { } option)
            _viewModel.SetSourceKind(option.Name);
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<SourceCandidate>().FirstOrDefault() is { } candidate)
            _viewModel.SelectSource(candidate);
    }

    private void OnSpanChecked(object sender, RoutedEventArgs e) => _viewModel.IsSpanActive = true;

    private void OnSpanUnchecked(object sender, RoutedEventArgs e) => _viewModel.IsSpanActive = false;

    private void OnChooseImageRequested(DisplayCardViewModel monitor)
    {
        var path = PickImage($"Choose wallpaper for {monitor.DisplayName} ({monitor.DeviceName})");
        if (path is null)
            return;

        // Staging only: the page's single Apply button performs the change.
        _viewModel.SetDraftFilePath(path);
    }

    private void OnChooseSpanImageRequested()
    {
        var path = PickImage("Choose one wallpaper for all displays (Span)");
        if (path is null)
            return;

        _viewModel.ApplySpanWallpaper(path);
    }

    /// <summary>The same picker options used by the existing wallpaper flows.</summary>
    private string? PickImage(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Wallpaper images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var owner = Window.GetWindow(this);
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == true ? dialog.FileName : null;
    }
}
