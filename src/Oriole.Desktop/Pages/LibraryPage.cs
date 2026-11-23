using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oriole.Core.Services;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.Pages;

public sealed class LibraryPage : UserControl
{
    private readonly WallpaperLibraryStore _store = new();
    private readonly TextBox _search = new() { MinWidth = 240, Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _favoritesOnly = new()
    {
        Content = "Favorites only",
        Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 12, 0)
    };
    private readonly ComboBox _displayPicker = new() { MinWidth = 200, Height = 34 };
    private readonly Button _refresh = new() { Content = "Refresh", Padding = new Thickness(12, 7, 12, 7) };
    private readonly TextBlock _status = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly WrapPanel _cards = new();
    private IReadOnlyList<WallpaperLibraryEntry> _entries = Array.Empty<WallpaperLibraryEntry>();

    public LibraryPage()
    {
        LoadDisplays();
        Content = BuildLayout();
        _refresh.Click += async (_, _) => await ReloadAsync();
        _search.TextChanged += (_, _) => RenderEntries();
        _favoritesOnly.Checked += (_, _) => RenderEntries();
        _favoritesOnly.Unchecked += (_, _) => RenderEntries();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel();
        var header = new StackPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.Children.Add(new TextBlock
        {
            Text = "Search library",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        toolbar.Children.Add(_search);
        toolbar.Children.Add(_favoritesOnly);
        toolbar.Children.Add(_refresh);
        header.Children.Add(toolbar);

        var assignment = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        assignment.Children.Add(new TextBlock
        {
            Text = "Apply target",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        });
        assignment.Children.Add(_displayPicker);
        assignment.Children.Add(_status);
        header.Children.Add(assignment);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _cards
        });
        return root;
    }

    private void LoadDisplays()
    {
        try
        {
            var monitors = WindowsDesktopWallpaper.GetMonitors();
            foreach (var monitor in monitors)
                _displayPicker.Items.Add(monitor);
            _displayPicker.DisplayMemberPath = nameof(DisplayMonitor.DeviceName);
            _displayPicker.SelectedItem = monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                ?? monitors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _status.Text = $"Display detection unavailable: {ex.Message}";
        }
    }

    private async Task ReloadAsync()
    {
        _refresh.IsEnabled = false;
        _status.Text = "Loading library…";
        try
        {
            _entries = await _store.GetAllAsync();
            RenderEntries();
            _status.Text = $"{_entries.Count} saved wallpaper(s).";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read the library: {ex.Message}";
        }
        finally
        {
            _refresh.IsEnabled = true;
        }
    }

    private void RenderEntries()
    {
        var query = _search.Text.Trim();
        var filtered = _entries.Where(entry =>
                (!_favoritesOnly.IsChecked.GetValueOrDefault() || entry.IsFavorite)
                && (query.Length == 0
                    || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Collection.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        _cards.Children.Clear();
        if (filtered.Length == 0)
        {
            _cards.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 42, 57)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Child = new TextBlock
                {
                    Text = _entries.Count == 0
                        ? "Your library is empty. Save wallpapers from Discover to keep them available offline."
                        : "No saved wallpapers match this filter.",
                    Foreground = new SolidColorBrush(Color.FromRgb(169, 184, 204)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 700
                }
            });
            return;
        }

        foreach (var entry in filtered)
            _cards.Children.Add(CreateCard(entry));
    }

    private FrameworkElement CreateCard(WallpaperLibraryEntry entry)
    {
        var card = new Border
        {
            Width = 264,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(31, 43, 58)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 64, 82)),
            BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel();
        var image = new Image
        {
            Height = 150,
            Width = 244,
            Stretch = Stretch.UniformToFill
        };
        try
        {
            if (File.Exists(entry.LocalPath))
                image.Source = LoadBitmap(entry.LocalPath, 480);
        }
        catch
        {
            // A missing or damaged local image does not prevent its library entry being managed.
        }

        stack.Children.Add(new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(19, 27, 38)),
            Child = image
        });
        stack.Children.Add(new TextBlock
        {
            Text = entry.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 9, 0, 3)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{entry.Width:N0} × {entry.Height:N0} · {entry.Category} · {entry.Collection}",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 176, 197)),
            FontSize = 10,
            Margin = new Thickness(2, 0, 0, 8)
        });

        var actions = new WrapPanel();
        var apply = MakeButton("Set");
        var favorite = MakeButton(entry.IsFavorite ? "Unfavorite" : "Favorite");
        var open = MakeButton("Open file");
        var remove = MakeButton("Remove");
        apply.Margin = new Thickness(0, 0, 5, 5);
        favorite.Margin = new Thickness(0, 0, 5, 5);
        open.Margin = new Thickness(0, 0, 5, 5);
        remove.Margin = new Thickness(0, 0, 5, 5);

        apply.Click += (_, _) => ApplyEntry(entry);
        favorite.Click += async (_, _) =>
        {
            await _store.SetFavoriteAsync(entry.Id, !entry.IsFavorite);
            await ReloadAsync();
        };
        open.Click += (_, _) =>
        {
            try
            {
                if (!File.Exists(entry.LocalPath))
                    throw new FileNotFoundException("The image file is missing.", entry.LocalPath);
                Process.Start(new ProcessStartInfo(entry.LocalPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not open image: {ex.Message}";
            }
        };
        remove.Click += async (_, _) =>
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                "Remove this item from the Oriole library? The downloaded image file will be kept on disk.",
                "Remove library entry",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            await _store.RemoveAsync(entry.Id);
            await ReloadAsync();
        };

        actions.Children.Add(apply);
        actions.Children.Add(favorite);
        actions.Children.Add(open);
        actions.Children.Add(remove);
        stack.Children.Add(actions);
        card.Child = stack;
        return card;
    }

    private void ApplyEntry(WallpaperLibraryEntry entry)
    {
        try
        {
            if (_displayPicker.SelectedItem is not DisplayMonitor monitor)
                throw new InvalidOperationException("No display is selected.");
            if (!File.Exists(entry.LocalPath))
                throw new FileNotFoundException("The saved wallpaper file is missing.", entry.LocalPath);

            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, entry.LocalPath);
            _status.Text = $"Applied {entry.Title} to {monitor.DeviceName}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not apply wallpaper: {ex.Message}";
        }
    }

    private static BitmapImage LoadBitmap(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(9, 6, 9, 6),
        Background = new SolidColorBrush(Color.FromRgb(69, 104, 151)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(83, 123, 176)),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };
}
