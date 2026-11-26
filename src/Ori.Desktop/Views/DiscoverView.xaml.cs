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

        // PasswordBox does not support binding; bridge it to the view model explicitly.
        ApiKeyBox.PasswordChanged += OnApiKeyPasswordChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.ApiKey = ApiKeyBox.Password;

    private void OnSectionExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander)
            Dispatcher.BeginInvoke(new Action(() => expander.BringIntoView()));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Settings owns the stored key. Reload it on every visit so a key removed
        // there is not written back by a stale view-model value. The PasswordChanged
        // handler above will fire when assigning Password; refresh first, then assign.
        _viewModel.RefreshApiKey();
        ApiKeyBox.Password = _viewModel.ApiKey;
        _viewModel.OwnerWindow = Window.GetWindow(this);
        _viewModel.LoadDisplays();
        _viewModel.RefreshFeeds();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _viewModel.CancelSearch();
}
