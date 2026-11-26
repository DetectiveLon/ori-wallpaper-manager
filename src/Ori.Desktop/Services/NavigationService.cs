using System.Windows.Controls;
using Ori.Desktop.Views;

namespace Ori.Desktop.Services;

/// <summary>
/// Thin shell helper: maps a stable navigation key to the UserControl that
/// satisfies it. Navigation state lives in MainViewModel; this class only
/// resolves keys to views. Keys are matched case-insensitively.
/// </summary>
public sealed class NavigationService
{
    private readonly Dictionary<string, UserControl> _views = new(StringComparer.OrdinalIgnoreCase);

    public UserControl? CreateView(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (_views.TryGetValue(key, out var existing))
            return existing;

        UserControl? view = key.ToLowerInvariant() switch
        {
            "discover" => new DiscoverView(),
            "library" => new LibraryView(),
            "displays" => new DisplaysView(),
            "automation" => new AutomationView(),
            "settings" => new SettingsView(),
            _ => null
        };

        if (view is null)
            return null;
        _views[key] = view;
        return view;
    }

    /// <summary>
    /// Returns an already-created view without creating one. Used at shutdown so
    /// rotation loops are stopped even when the user navigated away from Automation.
    /// </summary>
    public bool TryGetView(string key, out UserControl? view) =>
        _views.TryGetValue(key, out view);
}
