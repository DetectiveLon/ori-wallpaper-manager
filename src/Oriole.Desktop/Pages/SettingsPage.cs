using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Oriole.Desktop.Services;

namespace Oriole.Desktop.Pages;

/// <summary>Settings that actually do something in the preview build.</summary>
public sealed class SettingsPage : UserControl
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oriole");
    private static readonly string CacheDirectory = Path.Combine(DataRoot, "cache", "images");
    private static readonly string LibraryDirectory = Path.Combine(DataRoot, "library");
    private static readonly string LogDirectory = Path.Combine(DataRoot, "logs");

    private readonly PasswordBox _apiKey = new()
    {
        Height = 34,
        Padding = new Thickness(10, 6, 10, 6),
        Background = Brush("PanelAltBrush"),
        Foreground = Brush("TextBrush"),
        BorderBrush = Brush("MutedBrush"),
        PasswordChar = '•'
    };
    private readonly TextBlock _keyStatus = Status("No key stored.");
    private readonly TextBlock _cacheStatus = Status("No cached images yet.");
    private readonly TextBlock _status = Status(string.Empty);

    public SettingsPage()
    {
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildLayout()
        };
        Refresh();
    }

    private StackPanel BuildLayout()
    {
        var stack = new StackPanel();
        stack.Children.Add(Card("WALLHAVEN API KEY", BuildApiKeyCard()));
        stack.Children.Add(Card("LOCAL CACHE", BuildCacheCard()));
        stack.Children.Add(Card("DATA & ABOUT", BuildDataCard()));
        stack.Children.Add(_status);
        return stack;
    }

    private StackPanel BuildApiKeyCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Copy(
            "Optional. Stored encrypted for this Windows user and used only for Wallhaven requests that need it (for example NSFW results). Search works without a key for SFW content."));

        _apiKey.Password = ApiKeyStore.Load();
        _apiKey.PasswordChanged += (_, _) => _keyStatus.Text = "Unsaved changes.";
        var save = ActionButton("Save API key");
        save.Click += (_, _) =>
        {
            try
            {
                ApiKeyStore.Save(_apiKey.Password);
                Refresh();
                SetStatus("API key saved.");
            }
            catch (Exception ex)
            {
                SetStatus($"Could not save API key: {ex.Message}");
            }
        };

        var clear = ActionButton("Remove key");
        clear.Click += (_, _) =>
        {
            ApiKeyStore.Clear();
            _apiKey.Password = string.Empty;
            Refresh();
            SetStatus("Stored API key removed.");
        };

        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(save);
        panel.Children.Add(_apiKey);
        panel.Children.Add(row);
        panel.Children.Add(_keyStatus);
        return panel;
    }

    private StackPanel BuildCacheCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Copy(
            "Thumbnails and full-size downloads are cached to avoid repeat downloads. Clearing the cache never removes wallpapers you saved to your library."));

        var clear = ActionButton("Clear cached images");
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Margin = new Thickness(0, 10, 0, 6);
        clear.Click += (_, _) =>
        {
            try
            {
                var files = Directory.Exists(CacheDirectory)
                    ? Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories).ToArray()
                    : [];
                foreach (var file in files)
                    File.Delete(file);
                RefreshCacheStatus();
                SetStatus($"Removed {files.Length} cached file(s).");
            }
            catch (Exception ex)
            {
                SetStatus($"Could not clear cache: {ex.Message}");
            }
        };

        panel.Children.Add(clear);
        panel.Children.Add(_cacheStatus);
        return panel;
    }

    private StackPanel BuildDataCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Copy("Oriole keeps its data under your Windows user profile. Nothing is uploaded."));
        panel.Children.Add(Status($"Library: {Path.Combine(LibraryDirectory, "library.json")}"));
        panel.Children.Add(Status($"Schedules: {Path.Combine(DataRoot, "schedules", "schedules.json")}"));
        panel.Children.Add(Status($"Error log: {Path.Combine(LogDirectory, "errors.log")}"));
        panel.Children.Add(Status("Version 0.1.0 · preview build"));

        var link = new Hyperlink(new Run("View the source on GitHub"))
        {
            NavigateUri = new Uri("https://github.com/DetectiveLon/oriole-wallpaper-manager")
        };
        link.RequestNavigate += (_, _) => { };
        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 10, 0, 0), Inlines = { link } });

        var open = ActionButton("Open data folder");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 12, 0, 0);
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(DataRoot);
                Process.Start(new ProcessStartInfo { FileName = DataRoot, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus($"Could not open folder: {ex.Message}");
            }
        };
        panel.Children.Add(open);
        return panel;
    }

    private void Refresh()
    {
        _keyStatus.Text = ApiKeyStore.HasKey
            ? "A key is stored for this Windows user."
            : "No key stored. SFW search works without one.";
        RefreshCacheStatus();
    }

    private void RefreshCacheStatus()
    {
        try
        {
            var files = Directory.Exists(CacheDirectory)
                ? Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories).ToArray()
                : [];
            var bytes = files.Sum(file =>
            {
                try { return new FileInfo(file).Length; }
                catch { return 0L; }
            });
            _cacheStatus.Text = $"{files.Length} cached file(s), {bytes / 1024.0 / 1024.0:F1} MB.";
        }
        catch (Exception ex)
        {
            _cacheStatus.Text = $"Could not read cache: {ex.Message}";
        }
    }

    private void SetStatus(string message) => _status.Text = message;

    private Border Card(string title, StackPanel content) => new()
    {
        Background = Brush("PanelBrush"),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(20),
        Margin = new Thickness(0, 0, 0, 14),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = Brush("AccentBrush"),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold
                },
                content
            }
        }
    };

    private static TextBlock Copy(string text) => new()
    {
        Text = text,
        Foreground = Brush("MutedBrush"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
        MaxWidth = 720
    };

    private static TextBlock Status(string text) => new()
    {
        Text = text,
        Foreground = Brush("MutedBrush"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static Button ActionButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(14, 8, 14, 8),
        Margin = new Thickness(8, 0, 0, 0),
        Background = Brush("PanelAltBrush"),
        Foreground = Brush("TextBrush"),
        BorderBrush = Brush("PanelAltBrush"),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static Brush Brush(string key) =>
        (Brush)Application.Current.FindResource(key);
}
