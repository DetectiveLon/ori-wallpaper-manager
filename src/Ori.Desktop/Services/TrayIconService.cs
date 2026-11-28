using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;

namespace Ori.Desktop.Services;

/// <summary>
/// Owns the Windows system-tray icon for Ori. The icon exposes a context
/// menu with "Open Ori" and "Exit"; their clicks are forwarded to the
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
    private const string OpenText = "Open Ori";
    private const string ExitText = "Exit";

    // NotifyIcon.Text is limited to 63 characters by the underlying shell API.
    private const int MaxTooltipLength = 63;

    // Balloon tips have their own, tighter shell limits. Bound both fields so a
    // long error message can never be rejected or truncated unpredictably.
    private const int MaxBalloonTitleLength = 63;
    private const int MaxBalloonMessageLength = 255;

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
            _notifyIcon!.Text = Cap(tooltip, MaxTooltipLength);
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
            _notifyIcon.Text = Cap(tooltip, MaxTooltipLength);
        });
    }

    /// <summary>
    /// Shows a short, non-intrusive balloon notification. Safe to call from any
    /// thread and before <see cref="Show"/>; a no-op while the icon is not ready
    /// (not created yet, hidden, or already disposed). Text is capped to the
    /// shell limits and truncated without splitting a Unicode surrogate pair.
    /// </summary>
    public void Notify(string title, string message, bool error = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        RunOnUiDispatcher(() =>
        {
            if (_disposed || _notifyIcon is null || !_notifyIcon.Visible)
                return;
            try
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    Cap(title, MaxBalloonTitleLength),
                    Cap(message, MaxBalloonMessageLength),
                    error ? ToolTipIcon.Error : ToolTipIcon.Info);
            }
            catch (Exception)
            {
                // A failed balloon must never affect the app or its tray icon.
            }
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
        // Restore on left click / double-click only. WinForms raises the plain
        // Click and DoubleClick events for right and middle clicks as well, so
        // using those here would reopen the window on a right-click and make it
        // impossible to leave it hidden. MouseClick/MouseDoubleClick carry the
        // button, and OpenFromTray is idempotent when a double-click also fires
        // a single click first.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                RunOnUiDispatcher(_onOpen);
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                RunOnUiDispatcher(_onOpen);
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

    /// <summary>
    /// Clips text to a shell character limit without leaving a dangling UTF-16
    /// high surrogate at the cut boundary, so all input stays valid Unicode.
    /// </summary>
    private static string Cap(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        var clipped = text[..maxLength];
        if (clipped.Length > 0 && char.IsHighSurrogate(clipped[^1]))
            clipped = clipped[..^1];
        return clipped;
    }

    private static void RunOnUiDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            InvokeGuarded(action);
            return;
        }

        // Tray callbacks arrive inside the WinForms NotifyIcon window procedure.
        // Running a WPF window operation inline there re-enters the WPF dispatcher
        // while it may be creating the window's presentation source, which can
        // fault with "The root Visual of a VisualTarget cannot have a parent".
        // Always post instead: the action runs as a normal dispatcher operation,
        // and any failure is handled by the app's dispatcher exception handler
        // rather than escaping as an unhandled Win32 callback exception.
        try
        {
            dispatcher.BeginInvoke(new Action(() => InvokeGuarded(action)));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is already shut down; nothing can be shown.
        }
    }

    /// <summary>Last line of defence: a tray callback must never escape into the
    /// WinForms message pump and raise the .NET unhandled-exception dialog.</summary>
    private static void InvokeGuarded(Action action)
    {
        try { action(); }
        catch (Exception ex) { App.ReportFailure(ex); }
    }
}
