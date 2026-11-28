using System.Windows;
using System.Windows.Controls;
using Ori.Desktop.ViewModels;

namespace Ori.Desktop.Views;

/// <summary>XAML host for the Wallhaven discovery experience.</summary>
public partial class DiscoverView : UserControl
{
    private readonly DiscoverViewModel _viewModel;

    public DiscoverView()
    {
        InitializeComponent();
        _viewModel = new DiscoverViewModel();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnSectionExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander)
            Dispatcher.BeginInvoke(new Action(() => expander.BringIntoView()));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Settings owns the stored key. Reload it on every visit so a key removed
        // there is not reused by a stale view-model value during an in-session search.
        _viewModel.RefreshApiKey();
        _viewModel.OwnerWindow = Window.GetWindow(this);
        _viewModel.LoadDisplays();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _viewModel.CancelSearch();
}
