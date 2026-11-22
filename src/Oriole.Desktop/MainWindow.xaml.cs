using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Oriole.Desktop.Services;

namespace Oriole.Desktop;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Subtitle, string Description)> Pages =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Overview"] = ("Your desktop, in order.", "Choose what you want to see. Or let Oriole take care of the rotation.", "Your workspace overview will live here."),
            ["Discover"] = ("Find something worth keeping.", "Search first. Filter properly. Preview before anything touches your desktop.", "The discovery browser is the next feature module: Wallhaven search, category and purity filters, tags, resolution, aspect ratio, sorting, and pagination."),
            ["Library"] = ("Your wallpaper library.", "Saved images stay on your PC and remain available offline.", "Collections, favourites, duplicates, metadata, and local folders will be managed here."),
            ["Displays"] = ("One setup for every display.", "See your actual monitor layout and configure each screen independently.", "Choose and apply a different image to each connected monitor."),
            ["Automation"] = ("Rotation, without timer surprises.", "A visible countdown, one pending change at a time, and proper pause handling.", "Per-display schedules, next-run status, pause/resume, previous/next, and fullscreen-game deferral are being implemented in the scheduling module."),
            ["Settings"] = ("Preferences.", "Keep the controls that matter. Leave the rest out of the way.", "Theme, startup behaviour, cache limits, download folder, API keys, and privacy controls will live here.")
        };

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || !Pages.TryGetValue(key, out var page))
            return;

        PageTitle.Text = page.Title;
        PageSubtitle.Text = page.Subtitle;
        PageBody.Children.Clear();

        if (string.Equals(key, "Displays", StringComparison.OrdinalIgnoreCase))
            BuildDisplaysPage();
        else
            BuildPlaceholderPage(key, page.Description);

        foreach (var button in FindVisualChildren<Button>(this))
        {
            var isSelected = string.Equals(button.Tag as string, key, StringComparison.OrdinalIgnoreCase);
            button.Background = isSelected
                ? (Brush)FindResource("PanelAltBrush")
                : Brushes.Transparent;
            button.Foreground = isSelected
                ? (Brush)FindResource("TextBrush")
                : (Brush)FindResource("MutedBrush");
        }
    }

    private void BuildPlaceholderPage(string key, string description)
    {
        var panel = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = key.ToUpperInvariant(),
                        Foreground = (Brush)FindResource("AccentBrush"),
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    },
                    new TextBlock
                    {
                        Text = description,
                        Foreground = (Brush)FindResource("TextBrush"),
                        FontSize = 15,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 12, 0, 0),
                        MaxWidth = 760
                    }
                }
            }
        };

        PageBody.Children.Add(panel);
    }

    private void BuildDisplaysPage()
    {
        var container = new StackPanel();
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 16) };
        var refreshButton = new Button
        {
            Content = "Refresh displays",
            Padding = new Thickness(16, 9, 16, 9),
            Background = (Brush)FindResource("PanelAltBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("PanelAltBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var status = new TextBlock
        {
            Foreground = (Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(refreshButton, Dock.Right);
        toolbar.Children.Add(refreshButton);
        toolbar.Children.Add(status);

        var cards = new StackPanel();
        container.Children.Add(toolbar);
        container.Children.Add(cards);
        PageBody.Children.Add(container);

        void LoadDisplays()
        {
            cards.Children.Clear();
            try
            {
                var monitors = WindowsDesktopWallpaper.GetMonitors();
                if (monitors.Count == 0)
                {
                    status.Text = "Windows did not report any connected displays.";
                    return;
                }

                foreach (var monitor in monitors)
                    cards.Children.Add(CreateDisplayCard(monitor, status, LoadDisplays));

                status.Text = $"{monitors.Count} display(s) detected. Image assignment is independent per display; Windows currently uses one shared fit mode.";
            }
            catch (Exception ex)
            {
                status.Text = "Could not read display information.";
                MessageBox.Show(this, ex.Message, "Display detection failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        refreshButton.Click += (_, _) => LoadDisplays();
        LoadDisplays();
    }

    private Border CreateDisplayCard(DisplayMonitor monitor, TextBlock status, Action refresh)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = monitor.IsPrimary ? $"{monitor.DeviceName}  ·  PRIMARY" : monitor.DeviceName,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{monitor.Width:N0} × {monitor.Height:N0}  ·  Position {monitor.Left}, {monitor.Top}",
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0)
        });
        details.Children.Add(new TextBlock
        {
            Text = monitor.CurrentWallpaper is null ? "No wallpaper path reported by Windows" : monitor.CurrentWallpaper,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 18, 0)
        });

        var chooseButton = new Button
        {
            Content = "Choose image",
            Padding = new Thickness(16, 10, 16, 10),
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.White,
            BorderBrush = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        chooseButton.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Choose wallpaper for {monitor.DeviceName}",
                Filter = "Wallpaper images|*.jpg;*.jpeg;*.png;*.bmp;*.dib;*.tif;*.tiff|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, dialog.FileName);
                status.Text = $"Wallpaper applied to {monitor.DeviceName}.";
                refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not apply wallpaper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        Grid.SetColumn(chooseButton, 1);
        layout.Children.Add(details);
        layout.Children.Add(chooseButton);
        card.Child = layout;
        return card;
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
