using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;

namespace Oriole.Desktop.Services;

/// <summary>
/// Owns the Windows system-tray icon for Oriole. The icon exposes a context
/// menu with "Open Oriole" and "Exit"; their clicks are forwarded to the
/// callbacks supplied by the host.
/// </summary>
/// <remarks>
/// WinForms objects are created, updated and disposed only on the WPF UI
/// dispatcher, which owns the message loop a NotifyIcon depends on. Callers
/// may therefore use this service from any thread. The host must enable
/// Windows Forms in the project file (&lt;UseWindowsForms&gt;true&lt;/UseWindowsForms&gt;).
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private const string OpenText = "Open Oriole";
    private const string ExitText = "Exit";

    // NotifyIcon.Text is limited to 63 characters by the underlying shell API.
    private const int MaxTooltipLength = 63;

    private readonly Action _onOpen;
    private readonly Action _onExit;

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private Icon? _icon;
    private bool _ownsIcon;
    private bool _disposed;

    public TrayIconService(Action onOpen, Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onOpen);
        ArgumentNullException.ThrowIfNull(onExit);
        _onOpen = onOpen;
        _onExit = onExit;
    }

    /// <summary>Makes the tray icon visible with the supplied tooltip.</summary>
    public void Show(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        RunOnUiDispatcher(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureCreated();
            _notifyIcon!.Text = Cap(tooltip);
            _notifyIcon.Visible = true;
        });
    }

    /// <summary>Updates the tooltip. Safe to call before <see cref="Show"/>.</summary>
    public void UpdateTooltip(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        RunOnUiDispatcher(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_notifyIcon is null)
                return;
            _notifyIcon.Text = Cap(tooltip);
        });
    }

    public void Dispose()
    {
        RunOnUiDispatcher(DisposeCore);
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _contextMenu?.Dispose();
        _contextMenu = null;

        if (_icon is not null)
        {
            if (_ownsIcon)
                _icon.Dispose();
            _icon = null;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Created objects are stored in fields and disposed in DisposeCore.")]
    private void EnsureCreated()
    {
        if (_notifyIcon is not null)
            return;

        var (icon, ownsIcon) = LoadIcon();
        _icon = icon;
        _ownsIcon = ownsIcon;

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem(OpenText);
        openItem.Click += (_, _) => RunOnUiDispatcher(_onOpen);
        var exitItem = new ToolStripMenuItem(ExitText);
        exitItem.Click += (_, _) => RunOnUiDispatcher(_onExit);
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _contextMenu = menu;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            ContextMenuStrip = menu,
            Text = string.Empty,
            Visible = false
        };
    }

    /// <summary>
    /// Uses the application's own icon when available; falls back to a shared
    /// stock icon that must not be disposed by this service.
    /// </summary>
    private static (Icon Icon, bool Owns) LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                    return (extracted, true);
            }
        }
        catch (Exception)
        {
            // A missing or unreadable executable icon is not fatal.
        }

        return (SystemIcons.Application, false);
    }

    private static string Cap(string tooltip) =>
        tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..MaxTooltipLength];

    private static void RunOnUiDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
