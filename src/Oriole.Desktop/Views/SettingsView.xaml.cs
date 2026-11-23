using System.Diagnostics;
using Oriole.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace Oriole.Desktop.Views;

/// <summary>XAML view for Settings. Code-behind only bridges the non-bindable PasswordBox and the hyperlink.</summary>
public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel();
        DataContext = _viewModel;
        // NavigationService caches views: Loaded/Unloaded fire on every visit. Subscribe
        // to the Loaded cycle here and (re)attach the callbacks on each load so typing a
        // key or removing it keeps working after navigating away and back.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.PasswordChanged -= OnApiKeyPasswordChanged; // no duplicate hooks
        ApiKeyBox.PasswordChanged += OnApiKeyPasswordChanged;
        _viewModel.ApiKeyCleared -= OnApiKeyCleared;
        _viewModel.ApiKeyCleared += OnApiKeyCleared;

        _viewModel.RefreshApiKey();
        ApiKeyBox.Password = _viewModel.ApiKey;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.PasswordChanged -= OnApiKeyPasswordChanged;
        _viewModel.ApiKeyCleared -= OnApiKeyCleared;
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.ApiKey = ApiKeyBox.Password;

    private void OnApiKeyCleared() => ApiKeyBox.Password = string.Empty;

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _viewModel.Status = $"Could not open link: {ex.Message}";
        }
        e.Handled = true;
    }
}
