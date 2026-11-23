using Oriole.Core.Models;
using Oriole.Core.Services;
using Oriole.Desktop.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oriole.Desktop.Views;

/// <summary>Wallhaven discovery and direct-to-display wallpaper selection.</summary>
public sealed class DiscoverPage : UserControl
{
    private static readonly WallhavenClient Api = new(WallhavenClient.CreateHttpClient());
    private static readonly WallhavenImageDownloader Images = new();
    private static readonly WallpaperImageCache LibraryImages = new();
    private static readonly WallpaperLibraryStore Library = new();

    private readonly CheckBox _general = Check("General", true);
    private readonly CheckBox _anime = Check("Anime", true);
    private readonly CheckBox _people = Check("People", true);
    private readonly CheckBox _safe = Check("SFW", true);
    private readonly CheckBox _sketchy = Check("Sketchy");
    private readonly CheckBox _explicit = Check("NSFW");
    private readonly TextBox _tags = CreateTextBox("Example: landscape, nature, -people");
    private readonly TextBox _minimum = CreateTextBox("1920x1080");
    private readonly TextBox _exact = CreateTextBox("3840x2160");
    private readonly TextBox _colors = CreateTextBox("Custom hex colors, comma-separated");
    private readonly WrapPanel _colorPalette = new();
    private readonly PasswordBox _apiKey = new() { Height = 32, Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8, 5, 8, 5), Background = Brush("#202B39"), Foreground = Brush("#F1F4F8"), BorderBrush = Brush("#344254") };
    private readonly ComboBox _sorting = CreateCombo();
    private readonly ComboBox _topRange = CreateCombo();
    private readonly ComboBox _order = CreateCombo();
    private readonly ComboBox _ratio = CreateCombo();
    private readonly ComboBox _display = CreateCombo();
    private readonly Button _searchButton = CreateButton("Search wallpapers", true);
    private readonly Button _previous = CreateButton("Previous");
    private readonly Button _next = CreateButton("Next");
    private readonly TextBlock _status = Label("Choose filters and search.", 11, "#9AA8B8");
    private readonly TextBlock _pageText = Label("", 11, "#9AA8B8");
    private readonly WrapPanel _cards = new();
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private WallhavenSearchPage? _currentPage;
    private WallhavenFilters? _currentFilters;
    private CancellationTokenSource? _searchCts;
    private bool _searching;

    public DiscoverPage()
    {
        ConfigureChoices();
        ConfigureColorPalette();
        Content = BuildLayout();
        _searchButton.Click += async (_, _) => await SearchFirstPageAsync();
        _previous.Click += async (_, _) => await ChangePageAsync(-1);
        _next.Click += async (_, _) => await ChangePageAsync(1);
        Loaded += (_, _) => LoadDisplays();
        Unloaded += (_, _) => CancelSearch();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(286) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var filterStack = new StackPanel();
        filterStack.Children.Add(Label("SEARCH WALLHAVEN", 10, "#9ABEFF", FontWeights.Bold));
        filterStack.Children.Add(Label("Find your wallpaper", 19, "#F1F4F8", FontWeights.SemiBold, new Thickness(0, 7, 0, 14)));
        AddSection(filterStack, "Tags");
        filterStack.Children.Add(StyleTextBox(_tags));
        filterStack.Children.Add(Label("Separate tags with commas. Prefix with - to exclude; + to require.", 10, "#9AA8B8", margin: new Thickness(0, 4, 0, 12)));
        AddSection(filterStack, "Categories");
        filterStack.Children.Add(CheckRow(_general, _anime, _people));
        AddSection(filterStack, "Content");
        filterStack.Children.Add(CheckRow(_safe, _sketchy, _explicit));
        filterStack.Children.Add(Label("NSFW results require your Wallhaven API key.", 10, "#9AA8B8", margin: new Thickness(0, 5, 0, 0)));
        filterStack.Children.Add(_apiKey);
        AddSection(filterStack, "Sorting");
        filterStack.Children.Add(StyleCombo(_sorting));
        filterStack.Children.Add(Label("Toplist period", 11, "#9AA8B8", margin: new Thickness(0, 9, 0, 0)));
        filterStack.Children.Add(StyleCombo(_topRange));
        filterStack.Children.Add(Label("Order", 11, "#9AA8B8", margin: new Thickness(0, 9, 0, 0)));
        filterStack.Children.Add(StyleCombo(_order));
        AddSection(filterStack, "Minimum resolution");
        filterStack.Children.Add(StyleTextBox(_minimum));
        AddSection(filterStack, "Exact resolution");
        filterStack.Children.Add(StyleTextBox(_exact));
        AddSection(filterStack, "Aspect ratio");
        filterStack.Children.Add(StyleCombo(_ratio));
        AddSection(filterStack, "Colours (select palette colours or enter custom hex values)");
        filterStack.Children.Add(new ScrollViewer
        {
            MaxHeight = 105,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _colorPalette
        });
        filterStack.Children.Add(StyleTextBox(_colors));
        _searchButton.Margin = new Thickness(0, 16, 0, 0);
        filterStack.Children.Add(_searchButton);
        filterStack.Children.Add(Label("Your API key is used only for this request and is not stored.", 10, "#9AA8B8", margin: new Thickness(0, 9, 0, 0)));

        var sidebar = new Border
        {
            Background = Brush("#1A222E"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = filterStack }
        };
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var displayPicker = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        displayPicker.Children.Add(Label("Apply to", 11, "#9AA8B8", margin: new Thickness(0, 0, 8, 0)));
        _display.Width = 172;
        displayPicker.Children.Add(StyleCombo(_display));
        DockPanel.SetDock(displayPicker, Dock.Right);
        toolbar.Children.Add(displayPicker);
        toolbar.Children.Add(_status);
        Grid.SetRow(toolbar, 0);
        right.Children.Add(toolbar);

        var resultScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _cards };
        Grid.SetRow(resultScroll, 1);
        right.Children.Add(resultScroll);

        var paging = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _previous.Padding = new Thickness(12, 7, 12, 7);
        _previous.IsEnabled = false;
        _next.Padding = new Thickness(12, 7, 12, 7);
        _next.IsEnabled = false;
        DockPanel.SetDock(_previous, Dock.Left);
        DockPanel.SetDock(_next, Dock.Right);
        paging.Children.Add(_previous);
        paging.Children.Add(_next);
        _pageText.HorizontalAlignment = HorizontalAlignment.Center;
        _pageText.VerticalAlignment = VerticalAlignment.Center;
        paging.Children.Add(_pageText);
        Grid.SetRow(paging, 2);
        right.Children.Add(paging);

        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        return root;
    }

    private void ConfigureChoices()
    {
        AddChoice(_sorting, "Random", WallhavenSort.Random, true);
        AddChoice(_sorting, "Date added", WallhavenSort.DateAdded);
        AddChoice(_sorting, "Relevance", WallhavenSort.Relevance);
        AddChoice(_sorting, "Most viewed", WallhavenSort.Views);
        AddChoice(_sorting, "Most favourited", WallhavenSort.Favorites);
        AddChoice(_sorting, "Toplist", WallhavenSort.Toplist);

        AddChoice(_topRange, "Day", WallhavenTopRange.Day);
        AddChoice(_topRange, "3 days", WallhavenTopRange.ThreeDays);
        AddChoice(_topRange, "Week", WallhavenTopRange.Week);
        AddChoice(_topRange, "Month", WallhavenTopRange.Month, true);
        AddChoice(_topRange, "3 months", WallhavenTopRange.ThreeMonths);
        AddChoice(_topRange, "6 months", WallhavenTopRange.SixMonths);
        AddChoice(_topRange, "Year", WallhavenTopRange.Year);

        AddChoice(_order, "Descending", WallhavenOrder.Descending, true);
        AddChoice(_order, "Ascending", WallhavenOrder.Ascending);

        AddChoice(_ratio, "Any ratio", string.Empty, true);
        AddChoice(_ratio, "Landscape · 16:9", "16x9");
        AddChoice(_ratio, "Landscape · 16:10", "16x10");
        AddChoice(_ratio, "Ultrawide · 21:9", "21x9");
        AddChoice(_ratio, "Ultrawide · 32:9", "32x9");
        AddChoice(_ratio, "Super ultrawide · 48:9", "48x9");
        AddChoice(_ratio, "Portrait · 9:16", "9x16");
        AddChoice(_ratio, "Portrait · 10:16", "10x16");
        AddChoice(_ratio, "Portrait · 9:18", "9x18");
        AddChoice(_ratio, "Classic · 3:2", "3x2");
        AddChoice(_ratio, "Classic · 4:3", "4x3");
        AddChoice(_ratio, "Classic · 5:4", "5x4");
        AddChoice(_ratio, "Square · 1:1", "1x1");
    }


    private void ConfigureColorPalette()
    {
        foreach (var color in WallhavenColors.Allowed.OrderBy(value => value, StringComparer.Ordinal))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(2),
                Background = Brush("#" + color),
                BorderBrush = Brush("#718096"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = color,
                FontSize = 9,
                Foreground = Brush("#F1F4F8"),
                VerticalAlignment = VerticalAlignment.Center
            });

            _colorPalette.Children.Add(new ToggleButton
            {
                Content = content,
                Tag = color,
                ToolTip = $"Filter by #{color}",
                Padding = new Thickness(4),
                Margin = new Thickness(2),
                Background = Brush("#202B39"),
                Foreground = Brush("#F1F4F8"),
                BorderBrush = Brush("#344254"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            });
        }
    }

    private void LoadDisplays()
    {
        try
        {
            _monitors = WindowsDesktopWallpaper.GetMonitors();
            _display.ItemsSource = _monitors;
            _display.DisplayMemberPath = nameof(DisplayMonitor.DeviceName);
            _display.SelectedItem = _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
            if (_monitors.Count == 0)
                _status.Text = "No connected display detected. You can still browse wallpapers.";
        }
        catch (Exception ex)
        {
            _monitors = [];
            _status.Text = $"Display detection unavailable: {ex.Message}";
        }
    }

    private async Task SearchFirstPageAsync()
    {
        if (_searching)
            return;
        try
        {
            _currentFilters = BuildFilters();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }
        await SearchPageAsync(1, null);
    }

    private WallhavenFilters BuildFilters()
    {
        var categories = WallhavenCategory.None;
        if (_general.IsChecked == true) categories |= WallhavenCategory.General;
        if (_anime.IsChecked == true) categories |= WallhavenCategory.Anime;
        if (_people.IsChecked == true) categories |= WallhavenCategory.People;

        var purity = WallhavenPurity.None;
        if (_safe.IsChecked == true) purity |= WallhavenPurity.Safe;
        if (_sketchy.IsChecked == true) purity |= WallhavenPurity.Sketchy;
        if (_explicit.IsChecked == true) purity |= WallhavenPurity.Explicit;
        if (purity.HasFlag(WallhavenPurity.Explicit) && string.IsNullOrWhiteSpace(_apiKey.Password))
            throw new InvalidOperationException("Enter your Wallhaven API key to include NSFW results.");

        var sort = SelectedValue(_sorting, WallhavenSort.Random);
        var order = SelectedValue(_order, WallhavenOrder.Descending);
        var range = SelectedValue(_topRange, WallhavenTopRange.Month);
        var ratio = SelectedValue(_ratio, string.Empty);
        var customColors = _colors.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim().TrimStart('#'));
        var colors = _colorPalette.Children.OfType<ToggleButton>()
            .Where(item => item.IsChecked == true)
            .Select(item => item.Tag?.ToString() ?? string.Empty)
            .Concat(customColors)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filters = new WallhavenFilters
        {
            Query = NormalizeTags(_tags.Text),
            Categories = categories,
            Purity = purity,
            Sorting = sort,
            Order = order,
            TopRange = range,
            MinimumResolution = string.IsNullOrWhiteSpace(_minimum.Text) ? null : _minimum.Text.Trim(),
            ExactResolution = string.IsNullOrWhiteSpace(_exact.Text) ? null : _exact.Text.Trim(),
            Ratio = string.IsNullOrWhiteSpace(ratio) ? null : ratio,
            Colors = colors
        };
        filters.Validate();
        return filters;
    }

    private static string NormalizeTags(string value)
    {
        var input = value.Trim();
        if (!input.Contains(','))
            return input;
        return string.Join(' ', input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.StartsWith('-') || tag.StartsWith('+') ? tag : "+" + tag));
    }

    private async Task ChangePageAsync(int delta)
    {
        if (_searching || _currentPage is null || _currentFilters is null)
            return;
        var target = _currentPage.CurrentPage + delta;
        if (target < 1 || target > _currentPage.LastPage)
            return;
        await SearchPageAsync(target, _currentPage.Seed);
    }

    private async Task SearchPageAsync(int page, string? seed)
    {
        if (_currentFilters is null || _searching)
            return;
        CancelSearch();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _searching = true;
        _searchButton.IsEnabled = false;
        _previous.IsEnabled = false;
        _next.IsEnabled = false;
        _status.Text = "Searching Wallhaven…";
        _cards.Children.Clear();
        _pageText.Text = string.Empty;

        try
        {
            var filters = _currentFilters with { Page = page, Seed = seed };
            var result = await Api.SearchAsync(filters, _apiKey.Password, token);
            _currentPage = result;
            _currentFilters = filters with { Seed = result.Seed };
            foreach (var wallpaper in result.Items)
                _cards.Children.Add(CreateCard(wallpaper));

            _status.Text = result.Items.Count == 0
                ? "No wallpapers matched. Try removing a filter."
                : $"Found {result.Items.Count} wallpapers.";
            _pageText.Text = $"Page {result.CurrentPage} of {result.LastPage}";
            _previous.IsEnabled = result.CurrentPage > 1;
            _next.IsEnabled = result.CurrentPage < result.LastPage;
            await LoadPreviewsAsync(_cards.Children.OfType<Border>().ToList(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A navigation change cancels the in-flight request.
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _searching = false;
            _searchButton.IsEnabled = true;
        }
    }

    private Border CreateCard(WallhavenWallpaper wallpaper)
    {
        var card = new Border
        {
            Width = 214,
            Background = Brush("#1A222E"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 0, 12, 12),
            Tag = wallpaper
        };
        var stack = new StackPanel();
        var image = new Image { Width = 194, Height = 126, Stretch = Stretch.UniformToFill };
        var imageFrame = new Border
        {
            Height = 126,
            Background = Brush("#202B39"),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Child = image
        };
        stack.Children.Add(imageFrame);
        stack.Children.Add(Label($"{wallpaper.Width:N0} × {wallpaper.Height:N0}", 12, "#F1F4F8", FontWeights.SemiBold, new Thickness(1, 9, 0, 2)));
        stack.Children.Add(Label($"{wallpaper.Category} · {wallpaper.Purity} · {wallpaper.FileType}", 10, "#9AA8B8", margin: new Thickness(1, 0, 0, 8)));

        var actions = new UniformGrid { Columns = 2, Rows = 2 };
        var open = CreateButton("Open page");
        open.Margin = new Thickness(0, 0, 4, 4);
        open.Click += (_, _) => OpenWallpaperPage(wallpaper);

        var preview = CreateButton("Preview");
        preview.Margin = new Thickness(4, 0, 0, 4);
        preview.Click += async (_, _) => await PreviewWallpaperAsync(wallpaper, preview);

        var save = CreateButton("Save to library", true);
        save.Margin = new Thickness(0, 4, 4, 0);
        save.Click += async (_, _) => await SaveWallpaperAsync(wallpaper, save);

        var apply = CreateButton("Apply");
        apply.Margin = new Thickness(4, 4, 0, 0);
        apply.Click += async (_, _) => await ApplyWallpaperAsync(wallpaper, apply);

        actions.Children.Add(open);
        actions.Children.Add(preview);
        actions.Children.Add(save);
        actions.Children.Add(apply);
        stack.Children.Add(actions);
        card.Child = stack;
        card.Resources["PreviewImage"] = image;
        return card;
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<Border> cards, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = cards.Select(async card =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (card.Tag is not WallhavenWallpaper wallpaper || card.Resources["PreviewImage"] is not Image preview)
                    return;
                var download = await Images.DownloadAsync(wallpaper.ThumbnailUrl, WallhavenImageDownloader.MaximumThumbnailBytes, cancellationToken);
                var bitmap = DecodeImage(download.Bytes, 520);
                await Dispatcher.InvokeAsync(() => preview.Source = bitmap);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One unavailable preview should not hide the rest of the search results.
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }


    private async Task SaveWallpaperAsync(WallhavenWallpaper wallpaper, Button button)
    {
        button.IsEnabled = false;
        _status.Text = $"Saving wallpaper #{wallpaper.Id} to your library…";
        try
        {
            var path = await LibraryImages.SaveToLibraryAsync(wallpaper);
            await Library.UpsertAsync(new WallpaperLibraryEntry
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
            });
            _status.Text = $"Wallpaper #{wallpaper.Id} saved to your local library.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not save wallpaper #{wallpaper.Id}: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task PreviewWallpaperAsync(WallhavenWallpaper wallpaper, Button button)
    {
        button.IsEnabled = false;
        _status.Text = $"Loading full preview for wallpaper #{wallpaper.Id}…";
        try
        {
            var path = await LibraryImages.DownloadOriginalAsync(wallpaper);
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
                Background = Brush("#0F151E")
            };
            var frame = new Border
            {
                Margin = new Thickness(12),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(10),
                Background = Brush("#151D28"),
                Child = previewImage
            };
            previewWindow.Content = frame;
            _status.Text = $"Previewing wallpaper #{wallpaper.Id}.";
            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not preview wallpaper #{wallpaper.Id}: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
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

    private static BitmapSource DecodeImage(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("The downloaded preview does not contain an image frame.");

        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            || frame.PixelWidth > 32_768 || frame.PixelHeight > 32_768
            || (long)frame.PixelWidth * frame.PixelHeight > 50_000_000)
        {
            throw new InvalidDataException("The thumbnail dimensions exceed Oriole's image limits.");
        }

        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async Task ApplyWallpaperAsync(WallhavenWallpaper wallpaper, Button button)
    {
        if (_display.SelectedItem is not DisplayMonitor monitor)
        {
            _status.Text = "No target display is selected. Refresh the display list or reconnect a monitor.";
            return;
        }

        button.IsEnabled = false;
        _status.Text = $"Downloading wallpaper for {monitor.DeviceName}…";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var download = await Images.DownloadAsync(wallpaper.ImageUrl, WallhavenImageDownloader.MaximumWallpaperBytes, cancellation.Token);
            var extension = ValidateWallpaperImage(download.Bytes);
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oriole", "Wallpapers");
            Directory.CreateDirectory(directory);
            var id = string.Concat(wallpaper.Id.Where(char.IsAsciiLetterOrDigit));
            if (id.Length == 0)
                id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(directory, id + extension);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, download.Bytes, cancellation.Token);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, path);
            _status.Text = $"Wallpaper applied to {monitor.DeviceName}. Saved locally at {path}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not apply wallpaper: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string ValidateWallpaperImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("The downloaded file does not contain an image frame.");

        const int maxDimension = 32_768;
        const long maxPixels = 50_000_000;
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            || frame.PixelWidth > maxDimension || frame.PixelHeight > maxDimension
            || (long)frame.PixelWidth * frame.PixelHeight > maxPixels)
            throw new InvalidDataException($"Image dimensions {frame.PixelWidth:N0} × {frame.PixelHeight:N0} exceed Oriole's decode limit.");

        var codec = decoder.CodecInfo.FriendlyName;
        if (codec.Contains("JPEG", StringComparison.OrdinalIgnoreCase)) return ".jpg";
        if (codec.Contains("PNG", StringComparison.OrdinalIgnoreCase)) return ".png";
        if (codec.Contains("BMP", StringComparison.OrdinalIgnoreCase)) return ".bmp";
        if (codec.Contains("TIFF", StringComparison.OrdinalIgnoreCase)) return ".tiff";

        throw new InvalidDataException($"Image format '{codec}' is not supported yet.");
    }

    private void OpenWallpaperPage(WallhavenWallpaper wallpaper)
    {
        if (!WallhavenImageDownloader.IsTrustedUri(wallpaper.PageUrl))
        {
            _status.Text = "Wallhaven returned an unexpected page address.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(wallpaper.PageUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open the browser: {ex.Message}";
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    private static void AddSection(Panel panel, string title) =>
        panel.Children.Add(Label(title, 12, "#F1F4F8", FontWeights.SemiBold, new Thickness(0, 12, 0, 5)));

    private static TextBlock Label(string text, double size, string color, FontWeight? weight = null, Thickness? margin = null) =>
        new()
        {
            Text = text,
            FontSize = size,
            Foreground = Brush(color),
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0)
        };

    private static CheckBox Check(string text, bool selected = false) =>
        new() { Content = text, IsChecked = selected, Foreground = Brush("#F1F4F8"), Margin = new Thickness(0, 3, 12, 3) };

    private static UIElement CheckRow(params CheckBox[] boxes)
    {
        var row = new WrapPanel();
        foreach (var box in boxes)
            row.Children.Add(box);
        return row;
    }

    private static TextBox CreateTextBox(string placeholder) =>
        new()
        {
            Text = string.Empty,
            Height = 33,
            Padding = new Thickness(8, 5, 8, 5),
            Background = Brush("#202B39"),
            Foreground = Brush("#F1F4F8"),
            BorderBrush = Brush("#344254"),
            ToolTip = placeholder,
            Margin = new Thickness(0, 4, 0, 8)
        };

    private static TextBox StyleTextBox(TextBox box) => box;

    private static ComboBox CreateCombo() =>
        new()
        {
            Height = 32,
            Background = Brush("#202B39"),
            Foreground = Brush("#F1F4F8"),
            BorderBrush = Brush("#344254"),
            Margin = new Thickness(0, 4, 0, 8)
        };

    private static ComboBox StyleCombo(ComboBox combo) => combo;

    private static Button CreateButton(string title, bool primary = false) =>
        new()
        {
            Content = title,
            Padding = new Thickness(12, 8, 12, 8),
            Background = primary ? Brush("#9ABEFF") : Brush("#263344"),
            Foreground = primary ? Brush("#101722") : Brush("#F1F4F8"),
            BorderBrush = Brush("#344254"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal
        };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color)!);

    private static void AddChoice<T>(ComboBox combo, string label, T value, bool selected = false)
    {
        var item = new ComboBoxItem { Content = label, Tag = value };
        combo.Items.Add(item);
        if (selected)
            combo.SelectedItem = item;
    }

    private static T SelectedValue<T>(ComboBox combo, T fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;
}
