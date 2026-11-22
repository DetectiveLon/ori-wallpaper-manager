using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oriole.Core.Models;
using Oriole.Core.Services;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.Pages;

/// <summary>
/// Wallhaven discovery page. Search, filters, thumbnails, preview, download, save,
/// and per-display assignment are kept in one vertical slice until the UI layer grows
/// enough to justify a dedicated view-model project.
/// </summary>
public sealed class DiscoverPage : UserControl
{
    private readonly WallhavenClient _client = new(WallhavenClient.CreateHttpClient());
    private readonly WallpaperImageCache _imageCache = new();
    private readonly WallpaperLibraryStore _library = new();

    private readonly TextBox _query = new() { MinWidth = 250, Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _general = MakeCheckBox("General", true);
    private readonly CheckBox _anime = MakeCheckBox("Anime", true);
    private readonly CheckBox _people = MakeCheckBox("People", true);
    private readonly CheckBox _safe = MakeCheckBox("SFW", true);
    private readonly CheckBox _sketchy = MakeCheckBox("Sketchy", false);
    private readonly CheckBox _explicit = MakeCheckBox("NSFW", false);
    private readonly ComboBox _sorting = MakeCombo(Enum.GetValues<WallhavenSort>(), WallhavenSort.Random, 150);
    private readonly ComboBox _topRange = MakeCombo(Enum.GetValues<WallhavenTopRange>(), WallhavenTopRange.Month, 140);
    private readonly ComboBox _minimumResolution = MakeCombo(
        new[] { "Any", "1280x720", "1600x900", "1920x1080", "2560x1440", "3840x2160" }, "Any", 140);
    private readonly ComboBox _exactResolution = MakeCombo(
        new[] { "Any", "1280x720", "1600x900", "1920x1080", "2560x1440", "3440x1440", "3840x2160" }, "Any", 150);
    private readonly ComboBox _ratio = MakeCombo(
        new[] { "Any", "16x9", "16x10", "21x9", "32x9", "4x3", "5x4", "9x16", "1x1" }, "Any", 130);
    private readonly ListBox _colors = new()
    {
        Width = 270,
        Height = 72,
        SelectionMode = SelectionMode.Multiple,
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 72, 91)),
        Background = new SolidColorBrush(Color.FromRgb(25, 35, 48)),
        Foreground = Brushes.White,
        Padding = new Thickness(4)
    };
    private readonly PasswordBox _apiKey = new() { Width = 190, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _displayPicker = new() { MinWidth = 210, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _searchButton = MakeButton("Search");
    private readonly Button _previousButton = MakeButton("Previous");
    private readonly Button _nextButton = MakeButton("Next");
    private readonly TextBlock _status = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _pageStatus = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 203)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 8, 0)
    };
    private readonly WrapPanel _results = new() { Orientation = Orientation.Horizontal };
    private CancellationTokenSource? _searchCancellation;
    private WallhavenFilters? _activeFilters;
    private int _currentPage = 1;
    private int _lastPage = 1;

    public DiscoverPage()
    {
        foreach (var color in WallhavenColors.Allowed.OrderBy(value => value, StringComparer.Ordinal))
            _colors.Items.Add(color);

        _minimumResolution.SelectionChanged += (_, _) =>
        {
            if (!string.Equals(_minimumResolution.SelectedItem?.ToString(), "Any", StringComparison.Ordinal))
                _exactResolution.SelectedItem = "Any";
        };
        _exactResolution.SelectionChanged += (_, _) =>
        {
            if (!string.Equals(_exactResolution.SelectedItem?.ToString(), "Any", StringComparison.Ordinal))
                _minimumResolution.SelectedItem = "Any";
        };

        _searchButton.Click += async (_, _) => await SearchAsync(resetPage: true);
        _previousButton.Click += async (_, _) => await ChangePageAsync(_currentPage - 1);
        _nextButton.Click += async (_, _) => await ChangePageAsync(_currentPage + 1);

        LoadDisplays();
        Content = BuildLayout();
        ShowEmptyState("Search Wallhaven to find wallpapers. Results are previewed before they touch your desktop.");
        UpdatePagination();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var filters = BuildFilterPanel();
        Grid.SetRow(filters, 0);
        root.Children.Add(filters);

        var toolbar = new DockPanel { Margin = new Thickness(0, 12, 0, 12), LastChildFill = true };
        var displayArea = new StackPanel { Orientation = Orientation.Horizontal };
        displayArea.Children.Add(new TextBlock
        {
            Text = "Apply target",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        });
        displayArea.Children.Add(_displayPicker);
        DockPanel.SetDock(displayArea, Dock.Left);
        toolbar.Children.Add(displayArea);

        var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        statusPanel.Children.Add(_status);
        toolbar.Children.Add(statusPanel);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var resultsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _results
        };
        Grid.SetRow(resultsScroll, 2);
        root.Children.Add(resultsScroll);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_previousButton, Dock.Left);
        DockPanel.SetDock(_nextButton, Dock.Right);
        footer.Children.Add(_previousButton);
        footer.Children.Add(_nextButton);
        footer.Children.Add(_pageStatus);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildFilterPanel()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 57)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 64, 83)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(14)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "DISCOVER WALLHAVEN",
            Foreground = new SolidColorBrush(Color.FromRgb(151, 190, 244)),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 9)
        });

        var searchLine = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 9) };
        _searchButton.Margin = new Thickness(9, 0, 0, 0);
        _searchButton.MinWidth = 100;
        DockPanel.SetDock(_searchButton, Dock.Right);
        searchLine.Children.Add(_searchButton);
        searchLine.Children.Add(_query);
        stack.Children.Add(searchLine);

        var categoryLine = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        categoryLine.Children.Add(Labeled("Categories", new WrapPanel
        {
            Children = { _general, _anime, _people }
        }));
        categoryLine.Children.Add(Labeled("Content", new WrapPanel
        {
            Children = { _safe, _sketchy, _explicit }
        }));
        categoryLine.Children.Add(Labeled("Sort", _sorting));
        categoryLine.Children.Add(Labeled("Toplist period", _topRange));
        categoryLine.Children.Add(Labeled("API key (session only)", _apiKey));
        stack.Children.Add(categoryLine);

        var resolutionLine = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        resolutionLine.Children.Add(Labeled("Minimum resolution", _minimumResolution));
        resolutionLine.Children.Add(Labeled("Exact resolution", _exactResolution));
        resolutionLine.Children.Add(Labeled("Aspect ratio", _ratio));
        resolutionLine.Children.Add(Labeled("Colors (multi-select)", _colors));
        stack.Children.Add(resolutionLine);

        stack.Children.Add(new TextBlock
        {
            Text = "Use +tag for required tags and -tag to exclude tags. NSFW searches need a valid Wallhaven API key; the key is only kept in memory for this session.",
            Foreground = new SolidColorBrush(Color.FromRgb(154, 169, 190)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });

        border.Child = stack;
        return border;
    }

    private async Task SearchAsync(bool resetPage)
    {
        WallhavenFilters filters;
        try
        {
            filters = BuildFilters(resetPage ? 1 : _currentPage);
            if ((_explicit.IsChecked ?? false) && string.IsNullOrWhiteSpace(_apiKey.Password))
                throw new InvalidOperationException("Wallhaven requires an API key for NSFW searches.");

            if (resetPage)
                filters = filters with { Seed = null, Page = 1 };
            else if (_activeFilters is not null)
                filters = _activeFilters with { Page = _currentPage };

            await ExecuteSearchAsync(filters);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private WallhavenFilters BuildFilters(int page)
    {
        var categories = WallhavenCategory.None;
        if (_general.IsChecked == true) categories |= WallhavenCategory.General;
        if (_anime.IsChecked == true) categories |= WallhavenCategory.Anime;
        if (_people.IsChecked == true) categories |= WallhavenCategory.People;

        var purity = WallhavenPurity.None;
        if (_safe.IsChecked == true) purity |= WallhavenPurity.Safe;
        if (_sketchy.IsChecked == true) purity |= WallhavenPurity.Sketchy;
        if (_explicit.IsChecked == true) purity |= WallhavenPurity.Explicit;

        var minimum = _minimumResolution.SelectedItem?.ToString();
        var exact = _exactResolution.SelectedItem?.ToString();
        var ratio = _ratio.SelectedItem?.ToString();

        var filters = new WallhavenFilters
        {
            Query = _query.Text.Trim(),
            Categories = categories,
            Purity = purity,
            Sorting = _sorting.SelectedItem is WallhavenSort sort ? sort : WallhavenSort.Random,
            TopRange = _topRange.SelectedItem is WallhavenTopRange range ? range : WallhavenTopRange.Month,
            MinimumResolution = string.Equals(minimum, "Any", StringComparison.Ordinal) ? null : minimum,
            ExactResolution = string.Equals(exact, "Any", StringComparison.Ordinal) ? null : exact,
            Ratio = string.Equals(ratio, "Any", StringComparison.Ordinal) ? null : ratio,
            Colors = _colors.SelectedItems.Cast<string>().ToArray(),
            Page = page
        };
        filters.Validate();
        return filters;
    }

    private async Task ExecuteSearchAsync(WallhavenFilters filters)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        _searchButton.IsEnabled = false;
        _previousButton.IsEnabled = false;
        _nextButton.IsEnabled = false;
        _status.Text = "Searching Wallhaven…";

        try
        {
            var page = await _client.SearchAsync(filters, _apiKey.Password, token);
            _activeFilters = filters with { Seed = page.Seed, Page = page.CurrentPage };
            _currentPage = page.CurrentPage;
            _lastPage = Math.Max(page.CurrentPage, page.LastPage);
            _results.Children.Clear();

            if (page.Items.Count == 0)
                ShowEmptyState("No wallpapers matched those filters. Try broadening the search.");
            else
            {
                foreach (var wallpaper in page.Items)
                    _results.Children.Add(CreateWallpaperCard(wallpaper, token));

                var thumbnailTasks = _results.Children
                    .OfType<FrameworkElement>()
                    .Select(element => element.Tag as Task)
                    .Where(task => task is not null)
                    .Cast<Task>();
                await Task.WhenAll(thumbnailTasks);
            }

            _status.Text = $"{page.Items.Count} result(s) · page {page.CurrentPage} of {page.LastPage}";
            UpdatePagination();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search replaced this one.
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                _searchButton.IsEnabled = true;
            UpdatePagination();
        }
    }

    private FrameworkElement CreateWallpaperCard(WallhavenWallpaper wallpaper, CancellationToken token)
    {
        var card = new Border
        {
            Width = 252,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(31, 43, 58)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 64, 82)),
            BorderThickness = new Thickness(1)
        };
        var layout = new StackPanel();
        var image = new Image
        {
            Width = 232,
            Height = 142,
            Stretch = Stretch.UniformToFill,
            ClipToBounds = true
        };
        var imageFrame = new Border
        {
            Height = 142,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(19, 27, 38)),
            ClipToBounds = true,
            Child = image
        };
        layout.Children.Add(imageFrame);
        layout.Children.Add(new TextBlock
        {
            Text = $"#{wallpaper.Id}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 9, 0, 3)
        });
        layout.Children.Add(new TextBlock
        {
            Text = $"{wallpaper.Width:N0} × {wallpaper.Height:N0} · {wallpaper.Ratio} · {wallpaper.Category}",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 176, 197)),
            FontSize = 10,
            Margin = new Thickness(2, 0, 0, 8)
        });

        var actions = new WrapPanel();
        var preview = MakeButton("Preview");
        var save = MakeButton("Save");
        var apply = MakeButton("Set");
        preview.Margin = new Thickness(0, 0, 5, 0);
        save.Margin = new Thickness(0, 0, 5, 0);
        preview.Click += async (_, _) => await OpenPreviewAsync(wallpaper);
        save.Click += async (_, _) => await SaveWallpaperAsync(wallpaper);
        apply.Click += async (_, _) => await ApplyWallpaperAsync(wallpaper);
        actions.Children.Add(preview);
        actions.Children.Add(save);
        actions.Children.Add(apply);
        layout.Children.Add(actions);
        card.Child = layout;

        var thumbnailTask = LoadThumbnailAsync(wallpaper, image, token);
        card.Tag = thumbnailTask;
        return card;
    }

    private async Task LoadThumbnailAsync(
        WallhavenWallpaper wallpaper,
        Image target,
        CancellationToken cancellationToken)
    {
        await ThumbnailGate.WaitAsync(cancellationToken);
        try
        {
            var path = await _imageCache.DownloadThumbnailAsync(wallpaper, cancellationToken);
            target.Source = LoadBitmap(path, 480);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            target.ToolTip = "Thumbnail unavailable. Preview may still work.";
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    private static readonly SemaphoreSlim ThumbnailGate = new(4, 4);

    private async Task SaveWallpaperAsync(WallhavenWallpaper wallpaper)
    {
        try
        {
            _status.Text = $"Saving #{wallpaper.Id} to your library…";
            var path = await _imageCache.SaveToLibraryAsync(wallpaper);
            await _library.UpsertAsync(ToLibraryEntry(wallpaper, path));
            _status.Text = $"#{wallpaper.Id} saved to your library.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not save #{wallpaper.Id}: {ex.Message}";
        }
    }

    private async Task ApplyWallpaperAsync(WallhavenWallpaper wallpaper)
    {
        try
        {
            if (_displayPicker.SelectedItem is not DisplayMonitor monitor)
                throw new InvalidOperationException("No display is selected. Refresh Windows display detection and try again.");

            _status.Text = $"Downloading #{wallpaper.Id}…";
            var path = await _imageCache.DownloadOriginalAsync(wallpaper);
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, path);
            _status.Text = $"Applied #{wallpaper.Id} to {monitor.DeviceName}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not apply #{wallpaper.Id}: {ex.Message}";
        }
    }

    private async Task OpenPreviewAsync(WallhavenWallpaper wallpaper)
    {
        try
        {
            _status.Text = $"Loading full preview for #{wallpaper.Id}…";
            var path = await _imageCache.DownloadOriginalAsync(wallpaper);
            var previewImage = new Image
            {
                Source = LoadBitmap(path, 1800),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var previewWindow = new Window
            {
                Title = $"Oriole preview · #{wallpaper.Id}",
                Owner = Window.GetWindow(this),
                Width = 1050,
                Height = 760,
                MinWidth = 720,
                MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(15, 21, 30))
            };
            var root = new DockPanel { Margin = new Thickness(16) };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var save = MakeButton("Save to library");
            var apply = MakeButton("Apply to selected display");
            save.Margin = new Thickness(0, 0, 8, 0);
            save.Click += async (_, _) => await SaveWallpaperAsync(wallpaper);
            apply.Click += async (_, _) => await ApplyWallpaperAsync(wallpaper);
            actions.Children.Add(save);
            actions.Children.Add(apply);
            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);
            root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(21, 29, 40)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Child = previewImage
            });
            previewWindow.Content = root;
            _status.Text = $"Previewing #{wallpaper.Id}.";
            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not preview #{wallpaper.Id}: {ex.Message}";
        }
    }

    private static WallpaperLibraryEntry ToLibraryEntry(WallhavenWallpaper wallpaper, string path) => new()
    {
        Id = wallpaper.Id,
        Title = $"Wallhaven #{wallpaper.Id}",
        PageUrl = wallpaper.PageUrl.ToString(),
        ImageUrl = wallpaper.ImageUrl.ToString(),
        LocalPath = path,
        Category = wallpaper.Category,
        Purity = wallpaper.Purity,
        Width = wallpaper.Width,
        Height = wallpaper.Height,
        Ratio = wallpaper.Ratio,
        SavedAtUtc = DateTimeOffset.UtcNow,
        Collection = "Unsorted"
    };

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

    private void UpdatePagination()
    {
        _previousButton.IsEnabled = _currentPage > 1;
        _nextButton.IsEnabled = _currentPage < _lastPage;
        _pageStatus.Text = $"Page {_currentPage} / {_lastPage}";
    }

    private async Task ChangePageAsync(int page)
    {
        if (_activeFilters is null || page < 1 || page > _lastPage)
            return;

        _currentPage = page;
        await ExecuteSearchAsync(_activeFilters with { Page = page });
    }

    private void ShowEmptyState(string message)
    {
        _results.Children.Clear();
        _results.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 57)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(169, 184, 204)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 750
            }
        });
    }

    private static BitmapImage LoadBitmap(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 13, 5) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(174, 188, 208)),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(control);
        return panel;
    }

    private static CheckBox MakeCheckBox(string label, bool selected) => new()
    {
        Content = label,
        IsChecked = selected,
        Foreground = Brushes.White,
        Margin = new Thickness(0, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static ComboBox MakeCombo<T>(IEnumerable<T> values, T selected, double width)
    {
        var combo = new ComboBox
        {
            MinWidth = width,
            Height = 30,
            Padding = new Thickness(6, 3, 6, 3)
        };
        foreach (var value in values)
            combo.Items.Add(value);
        combo.SelectedItem = selected;
        return combo;
    }

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(11, 7, 11, 7),
        Background = new SolidColorBrush(Color.FromRgb(69, 104, 151)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(83, 123, 176)),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };
}
