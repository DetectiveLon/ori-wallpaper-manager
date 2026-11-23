using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Oriole.Desktop.ViewModels;
using Oriole.Desktop.Views;

namespace Oriole.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _shell = new();
    private AutomationView? _automationView;
    private bool _closingHandled;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        _shell.Navigate("Overview");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // With a tray icon available, minimizing means "keep running quietly".
        // The tray's Open Oriole menu restores the window.
        if (WindowState == WindowState.Minimized && App.Tray is not null)
        {
            ShowInTaskbar = false;
            Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingHandled || _automationView is null)
            return;

        // Bound wait: let rotation loops unwind before the dispatcher shuts down.
        _closingHandled = true;
        try
        {
            _automationView.RequestStop();
            _automationView.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutdown must finish even if a loop fails to stop cleanly.
        }
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        var view = _shell.Navigate(key);
        if (string.Equals(key, "Automation", StringComparison.OrdinalIgnoreCase))
            _automationView = view as AutomationView;

        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (button.Tag is not string buttonKey)
                continue;

            // The Overview CTA is a primary button with the same tag; only sidebar
            // navigation buttons should show the selected state.
            if (!ReferenceEquals(button.Style, FindResource("NavButtonStyle")))
                continue;

            var isSelected = string.Equals(buttonKey, key, StringComparison.OrdinalIgnoreCase);
            button.Background = isSelected
                ? (Brush)FindResource("PanelAltBrush")
                : Brushes.Transparent;
            button.Foreground = isSelected
                ? (Brush)FindResource("TextBrush")
                : (Brush)FindResource("MutedBrush");
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
