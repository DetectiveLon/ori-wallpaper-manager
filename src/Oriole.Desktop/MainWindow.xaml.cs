using System.Windows;
using System.Windows.Controls;

namespace Oriole.Desktop;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Subtitle, string Description)> Pages =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Overview"] = ("Your desktop, in order.", "Choose what you want to see. Or let Oriole take care of the rotation.", "Your workspace overview will live here."),
            ["Discover"] = ("Find something worth keeping.", "Search first. Filter properly. Preview before anything touches your desktop.", "The discovery browser is the next feature module: Wallhaven search, category and purity filters, tags, resolution, aspect ratio, sorting, and pagination."),
            ["Library"] = ("Your wallpaper library.", "Saved images stay on your PC and remain available offline.", "Collections, favourites, duplicates, metadata, and local folders will be managed here."),
            ["Displays"] = ("One setup for every display.", "See your actual monitor layout and configure each screen independently.", "Per-monitor assignment, preview, fit modes, and unified desktop mode are being built on top of the Windows desktop integration layer."),
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

        var panel = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = key.ToUpperInvariant(),
                        Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    },
                    new TextBlock
                    {
                        Text = page.Description,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                        FontSize = 15,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 12, 0, 0),
                        MaxWidth = 760
                    }
                }
            }
        };
        PageBody.Children.Add(panel);

        foreach (var button in FindVisualChildren<Button>(this))
        {
            var isSelected = string.Equals(button.Tag as string, key, StringComparison.OrdinalIgnoreCase);
            button.Background = isSelected
                ? (System.Windows.Media.Brush)FindResource("PanelAltBrush")
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = isSelected
                ? (System.Windows.Media.Brush)FindResource("TextBrush")
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
