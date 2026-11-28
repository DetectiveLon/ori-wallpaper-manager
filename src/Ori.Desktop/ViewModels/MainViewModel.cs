using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;
using Ori.Desktop.Views;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// A single navigation entry used by the shell. Key is the stable identifier
/// passed to <see cref="MainViewModel.Navigate"/>; Section, ToolTip and
/// AccessLabel are presentation hints consumed by the rail's DataTemplate
/// so navigation is defined in exactly one place.
/// </summary>
public sealed class NavigationItem
{
    public NavigationItem(string key, string label, string glyph)
        : this(key, label, glyph, string.Empty, label, label, 0)
    {
    }

    public NavigationItem(
        string key,
        string label,
        string glyph,
        string section,
        string toolTip,
        string accessLabel,
        int tabIndex)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Section = section;
        ToolTip = toolTip;
        AccessLabel = accessLabel;
        TabIndex = tabIndex;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public string Section { get; }
    public string ToolTip { get; }

    /// <summary>Label with the WPF "_" access-key marker, e.g. "_Discover".</summary>
    public string AccessLabel { get; }

    public int TabIndex { get; }
}

/// <summary>
/// Shell view model: owns the navigation list, the selected entry and the
/// currently displayed view. View creation is delegated to NavigationService.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;
    private NavigationItem? _selectedItem;
    private UserControl? _currentView;
    private string _title = "Your desktop, in order.";
    private string _subtitle = "Choose what you want to see. Or let Ori take care of the rotation.";

    public MainViewModel()
        : this(new NavigationService())
    {
    }

    public MainViewModel(NavigationService navigation)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

        Items = new ObservableCollection<NavigationItem>
        {
            // FIND & SAVE
            new("Discover", "Discover", "\uE721", "FIND & SAVE",
                "Search Wallhaven and save wallpapers (Alt+D)", "_Discover", 3),
            new("Library", "My library", "\uE8B7", "FIND & SAVE",
                "Browse wallpapers saved on this PC (Alt+L)", "My _library", 4),

            // YOUR DESKTOP
            // REMOVAL POINT: "Displays" is still in the rail for now. When the
            // page is retired, delete this item and its "displays" header case;
            // NavigationService's "displays" case can go with it.
            new("Displays", "Displays", "\uE7F4", "YOUR DESKTOP",
                "Choose an image for each monitor (Alt+P)", "Dis_plays", 5),
            new("Automation", "Auto rotation", "\uE8B3", "YOUR DESKTOP",
                "Cycle Wallhaven sources and per-display rotation (Alt+A)", "_Auto rotation", 6),

            // APP
            new("Settings", "Settings", "\uE713", "APP",
                "App preferences (Alt+S)", "_Settings", 7)
        };

        // One definition of navigation, two rail hosts: the grouped main list
        // takes the first four entries and the pinned footer takes Settings.
        // Items itself stays public and unchanged for existing consumers.
        PrimaryItems = new ObservableCollection<NavigationItem>(Items.Take(4));
        FooterItem = Items[4];

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string));

        // Discover is the landing page.
        SelectedItem = Items[0];
    }

    public ObservableCollection<NavigationItem> Items { get; }

    /// <summary>The four destinations rendered in the grouped main rail.</summary>
    public ObservableCollection<NavigationItem> PrimaryItems { get; }

    /// <summary>The pinned Settings destination rendered in the rail footer.</summary>
    public NavigationItem FooterItem { get; }

    public NavigationItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value) && value is not null)
                Navigate(value.Key);
        }
    }

    public UserControl? CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    /// <summary>
    /// The cached Automation view, if it has ever been opened. Shutdown uses this
    /// instead of CurrentView: rotation keeps running while the user visits other
    /// pages, so those loops must be stopped regardless of which page is visible.
    /// </summary>
    public AutomationView? AutomationView =>
        _navigation.TryGetView("Automation", out var view) ? view as AutomationView : null;

    /// <summary>
    /// Returns the cached Automation view, creating it on first use, without
    /// changing the visible page. Startup uses this to initialise saved rotation
    /// schedules even though Discover is the landing page.
    /// </summary>
    public AutomationView? EnsureAutomationView() =>
        _navigation.CreateView("Automation") as AutomationView;

    /// <summary>
    /// Refreshes the live Settings view model when a background check completes.
    /// Uses the cached view, so it does not create Settings or navigate to it.
    /// </summary>
    public void RefreshSettingsUpdateStatus()
    {
        if (_navigation.TryGetView("Settings", out var view) && view is SettingsView settingsView)
            settingsView.RefreshUpdateStatus();
    }

    public ICommand NavigateCommand { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    /// <summary>
    /// Resolves the key to a view, assigns it to CurrentView, syncs the
    /// selection without re-entering the setter, and returns that view.
    /// </summary>
    public UserControl? Navigate(string? key)
    {
        var view = _navigation.CreateView(key);

        // Give self-contained views a data context so their bindings work
        // without the host having to wire it up. Views that set their own
        // view model keep it.
        if (view is not null && view.DataContext is null)
            view.DataContext = this;

        CurrentView = view;

        (Title, Subtitle) = (key?.ToLowerInvariant()) switch
        {
            "discover" => ("Discover wallpapers.", "Search Wallhaven, preview results and save what you like."),
            "library" => ("Your wallpaper library.", "Saved images stay on your PC and remain available offline."),
            "displays" => ("Make every screen feel right.", "Arrange your monitors, pick an image for each, and set how it fits."),
            "automation" => ("Wallpapers that change on their own.", "Set a rhythm per display and cycle through Wallhaven sources automatically."),
            "settings" => ("Preferences.", "Keep the controls that matter. Leave the rest out of the way."),
            _ => ("Your desktop, in order.", "Choose what you want to see. Or let Ori take care of the rotation.")
        };

        if (key is not null)
        {
            var match = Items.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                if (!ReferenceEquals(match, _selectedItem))
                    SetProperty(ref _selectedItem, match);
            }
        }

        return view;
    }
}
