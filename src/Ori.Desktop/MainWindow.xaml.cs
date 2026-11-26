using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Ori.Desktop.ViewModels;
using Ori.Desktop.Views;

namespace Ori.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _shell = new();
    private AutomationView? _automationView;
    private bool _closingHandled;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
        Closing += OnClosing;
        // Discover is the landing page now; Overview was removed from the shell.
        _shell.Navigate("Discover");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!App.ExplicitExit)
        {
            // X / Alt+F4: keep the app (and its rotation loops) running in the tray.
            // Minimizing is a separate, ordinary taskbar minimize and never hides.
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            // One-time, non-intrusive hint so the user knows where the app went.
            App.ShowFirstTrayHintOnce();
            return;
        }

        // Tray Exit: let the window close and stop rotation loops on the way out.
        StopRotationForShutdown();
    }

    /// <summary>
    /// Stops rotation loops without blocking the UI dispatcher. The loops post
    /// their continuations back to the dispatcher, so a blocking wait here would
    /// stall them and force the bounded stop to time out. Bounded inside
    /// AutomationViewModel.StopAsync. Idempotent.
    /// </summary>
    internal async Task StopRotationForShutdownAsync()
    {
        if (_closingHandled || _automationView is null)
            return;

        _closingHandled = true;
        try
        {
            await _automationView.StopAsync();
        }
        catch (Exception)
        {
            // Shutdown must finish even if a loop fails to stop cleanly.
        }
    }

    /// <summary>
    /// Bounded shutdown fallback for a close path that did not already use the
    /// async variant. Idempotent: called by both the tray Exit action and the
    /// Closing handler so cleanup cannot run twice.
    /// </summary>
    internal void StopRotationForShutdown()
    {
        if (_closingHandled || _automationView is null)
            return;

        _closingHandled = true;
        try
        {
            _automationView.RequestStop();
            _automationView.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutdown must finish even if a loop fails to stop cleanly.
        }
    }

    // Custom window chrome -------------------------------------------------

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        var window = sender is DependencyObject element ? Window.GetWindow(element) ?? this : this;
        SystemCommands.MinimizeWindow(window);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        var window = sender is DependencyObject element ? Window.GetWindow(element) ?? this : this;
        if (window.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(window);
        else
            SystemCommands.MaximizeWindow(window);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var window = sender is DependencyObject element ? Window.GetWindow(element) ?? this : this;
        SystemCommands.CloseWindow(window);
    }

    // Maximized geometry ---------------------------------------------------

    // WindowStyle=None + WindowChrome.ResizeBorderThickness=6 otherwise makes a
    // maximized window extend past the monitor work area (over the taskbar) and
    // clip the custom title bar. Clamping WM_GETMINMAXINFO keeps both intact.
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(ClampMaximizedToWorkArea);
        ClampStartupSizeToWorkArea();
        RegisterCaptionAccessKeys();
    }

    /// <summary>
    /// Keeps a normal-size window inside the monitor's work area at startup. The
    /// WM_GETMINMAXINFO hook above only covers the maximised case; on short laptop
    /// displays the default (or a restored DPI-scaled) size could still dip below
    /// the taskbar and clip the pager. Never grows or affects a maximised window.
    /// </summary>
    private void ClampStartupSizeToWorkArea()
    {
        if (WindowState == WindowState.Maximized)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        if (dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0)
            return;

        var workWidth = (info.WorkArea.Right - info.WorkArea.Left) / dpi.DpiScaleX;
        var workHeight = (info.WorkArea.Bottom - info.WorkArea.Top) / dpi.DpiScaleY;
        if (workWidth > 0 && Width > workWidth)
            Width = Math.Max(MinWidth, workWidth);
        if (workHeight > 0 && Height > workHeight)
            Height = Math.Max(MinHeight, workHeight);
    }

    /// <summary>
    /// Alt+M / Alt+R / Alt+C activate the caption buttons, which are otherwise
    /// only reachable by keyboard through Tab now that focus is enabled.
    /// </summary>
    private void RegisterCaptionAccessKeys()
    {
        AccessKeyManager.Register("m", MinimizeButton);
        AccessKeyManager.Register("r", MaximizeButton);
        AccessKeyManager.Register("c", CloseButton);
    }

    private static IntPtr ClampMaximizedToWorkArea(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;

        var work = info.WorkArea;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition.X = work.Left;
        mmi.MaxPosition.Y = work.Top;
        mmi.MaxSize.X = work.Right - work.Left;
        mmi.MaxSize.Y = work.Bottom - work.Top;
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        // Caption buttons live in the title bar too; never start a drag from one.
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        // Double-click the custom title bar toggles maximize, as it does on a
        // standard window. Border has no MouseDoubleClick event, so use ClickCount.
        if (e.ClickCount == 2)
        {
            OnTitleBarMouseDoubleClick(sender, e);
            return;
        }
        // A maximized window is moved via the restore/maximize caption controls;
        // dragging it can throw and would let the custom title bar escape the
        // work area. Keep the title bar intact instead.
        if (WindowState == WindowState.Maximized)
            return;
        DragMove();
    }

    private void OnTitleBarMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // Navigation -----------------------------------------------------------

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        var view = _shell.Navigate(key);
        if (string.Equals(key, "Automation", StringComparison.OrdinalIgnoreCase))
            _automationView = view as AutomationView;

        // Selection styling is driven entirely by NavButtonStyle's DataTrigger
        // bound to _shell.SelectedItem.Key. Keep no imperative brush assignment
        // here so there is a single source of truth for the selected state.
    }
}
