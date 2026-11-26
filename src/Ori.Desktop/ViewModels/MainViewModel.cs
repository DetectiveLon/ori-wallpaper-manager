using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// A single navigation entry used by the shell. The host renders Label/Glyph;
/// Key is the stable identifier passed to <see cref="MainViewModel.Navigate"/>.
/// </summary>
public sealed class NavigationItem
{
    public NavigationItem(string key, string label, string glyph)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
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
            new("Discover", "Discover", "\uE721"),   // search
            new("Library", "My library", "\uE8B7"),  // library
            new("Displays", "Displays", "\uE7F4"),   // monitor
            new("Automation", "Automation", "\uE8B3"),// timer
            new("Settings", "Settings", "\uE713")    // settings
        };

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string));

        // Discover is the landing page.
        SelectedItem = Items[0];
    }

    public ObservableCollection<NavigationItem> Items { get; }

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
            "displays" => ("One setup for every display.", "See your actual monitor layout and choose an image for each screen."),
            "automation" => ("Independent rotation for every screen.", "Separate schedules, clear countdowns and full-screen pause detection."),
            "settings" => ("Preferences.", "Keep the controls that matter. Leave the rest out of the way."),
            _ => ("Your desktop, in order.", "Choose what you want to see. Or let Ori take care of the rotation.")
        };

        if (key is not null)
        {
            var match = Items.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

            if (match is not null && !ReferenceEquals(match, _selectedItem))
                SetProperty(ref _selectedItem, match);
        }

        return view;
    }
}
