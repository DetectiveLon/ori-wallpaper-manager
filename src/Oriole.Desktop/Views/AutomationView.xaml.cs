using System.Windows;
using System.Windows.Controls;
using Oriole.Desktop.ViewModels;

namespace Oriole.Desktop.Views;

/// <summary>
/// XAML view for per-display wallpaper automation. The view owns no rotation logic;
/// it delegates lifecycle and shutdown to <see cref="AutomationViewModel"/>.
/// </summary>
public partial class AutomationView : UserControl
{
    public AutomationView()
    {
        InitializeComponent();
        ViewModel = new AutomationViewModel();
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    public AutomationViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.InitializeAsync();
    }

    /// <summary>Cancels all rotation loops without waiting. Optional companion to StopAsync.</summary>
    public void RequestStop() => ViewModel.RequestStop();

    /// <summary>
    /// Bounded shutdown for MainWindow.Closing; waits at most three seconds for the
    /// rotation loops to unwind. Safe to call with .Wait(...) from Closing.
    /// </summary>
    public Task StopAsync() => ViewModel.StopAsync();
}
