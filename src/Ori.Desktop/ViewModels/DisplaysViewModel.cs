using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ori.Core.Models;
using Ori.Core.Services;
using Ori.Desktop.Mvvm;
using Ori.Desktop.Services;

namespace Ori.Desktop.ViewModels;

/// <summary>
/// View model for the Displays page: enumerates monitors, remembers each panel's
/// fit/source choices and applies wallpapers explicitly.
/// </summary>
public sealed class DisplaysViewModel : ViewModelBase, IDisposable
{
    // Diagram geometry. The canvas fits the virtual desktop into a readable
    // surface; cards keep a logical minimum size and the whole diagram is
    // scaled down as one unit, so nothing can overflow the host.
    private const double DiagramMaxWidth = 720;
    private const double DiagramMaxHeight = 380;
    private const double DiagramPadding = 16;
    private const double MinimumCardWidth = 260;
    private const double MinimumCardHeight = 260;

    private readonly DisplayPreferencesStore _preferences;
    private readonly List<SourceCandidate> _librarySources = new();
    private readonly List<SourceCandidate> _feedSources = new();
    private bool _disposed;

    private string _status = "Ready.";
    private WallpaperPositionOption? _selectedPositionOption;
    private bool _suppressFitSync;
    private DisplayCardViewModel? _selectedMonitor;
    private string _selectedSourceKindName = "File";
    private SourceCandidate? _selectedSource;
    private string _draftFilePath = string.Empty;
    private bool _isSpanActive;
    private double _layoutWidth = DiagramMaxWidth;
    private double _layoutHeight = 220;
    private double _diagramScale = 1;
    private double _originLeft;
    private double _originTop;
    private bool _originVisible;
    private bool _noMonitors;

    public DisplaysViewModel()
    {
        _preferences = DisplayPreferencesStore.Load();
        // The OS owns the actual Span state; a saved flag from a previous session
        // must not show a toggle that disagrees with Windows.
        try
        {
            _isSpanActive = WindowsDesktopWallpaper.GetPosition() == DesktopWallpaperPosition.Span;
            if (_isSpanActive != _preferences.SpanActive)
                _preferences.SetSpan(_isSpanActive, _preferences.SpanPreviousFit,
                    _preferences.SpanPreviousFingerprint, _preferences.SpanImagePath);
        }
        catch
        {
            _isSpanActive = _preferences.SpanActive;
        }

        RefreshCommand = new RelayCommand(_ => RefreshDisplays());
        SelectMonitorCommand = new RelayCommand(parameter =>
        {
            if (parameter is not DisplayCardViewModel monitor)
                return;

            SetSelectedMonitor(monitor, persist: true);
            Status = $"{monitor.DisplayName} is now the target display for Apply actions.";
        });
        ChooseImageCommand = new RelayCommand(
            _ =>
            {
                if (ChooseImageRequested is null)
                {
                    Status = "The file picker is unavailable. Re-open Displays and try again.";
                    return;
                }

                if (IsSpanActive)
                {
                    Status = "Span is active. Switch it off before choosing a per-display image.";
                    return;
                }

                SetSelectedMonitor(SelectedMonitor, persist: false);
                ChooseImageRequested.Invoke(SelectedMonitor!);
            },
            _ => !IsSpanActive && SelectedMonitor is not null);
        ApplyCommand = new RelayCommand(_ => Apply(), _ => CanApply);
        SetSpanWallpaperCommand = new RelayCommand(_ =>
        {
            if (ChooseSpanImageRequested is null)
            {
                Status = "The file picker is unavailable. Re-open Displays and try again.";
                return;
            }

            ChooseSpanImageRequested.Invoke();
        });

        DisplayDetection.DisplaysChanged += OnDisplaysChanged;
        SyncPositionSelection();
        RefreshDisplays();
    }

    public ObservableCollection<DisplayCardViewModel> Monitors { get; } = new();

    /// <summary>Fit choices only. Span is a separate explicit toggle.</summary>
    public IReadOnlyList<WallpaperPositionOption> PositionOptions { get; } = new[]
    {
        new WallpaperPositionOption(DisplayFit.Fill, "Fill"),
        new WallpaperPositionOption(DisplayFit.Fit, "Fit"),
        new WallpaperPositionOption(DisplayFit.Stretch, "Stretch"),
        new WallpaperPositionOption(DisplayFit.Center, "Center"),
        new WallpaperPositionOption(DisplayFit.Tile, "Tile")
    };

    public IReadOnlyList<SourceKindOption> SourceKindOptions { get; } = new[]
    {
        new SourceKindOption("File", "Image file"),
        new SourceKindOption("Library", "Library item"),
        new SourceKindOption("Feed", "Saved feed")
    };

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value, nameof(Status));
    }

    public ICommand RefreshCommand { get; }
    public ICommand SelectMonitorCommand { get; }
    public ICommand ChooseImageCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand SetSpanWallpaperCommand { get; }

    /// <summary>View hook: opens the file picker and sets a draft path (never applies).</summary>
    public event Action<DisplayCardViewModel>? ChooseImageRequested;

    /// <summary>View hook: opens the file picker for the single Span image.</summary>
    public event Action? ChooseSpanImageRequested;

    /// <summary>The remembered target display, falling back to primary after a refresh.</summary>
    public DisplayCardViewModel? SelectedMonitor => _selectedMonitor;

    /// <summary>The single page-level Apply is enabled only with a target and a draft source.</summary>
    public bool CanApply => !IsSpanActive && SelectedMonitor is not null && HasDraftSource;

    public bool CanChooseFit => !IsSpanActive && SelectedMonitor is not null;

    /// <summary>True while Windows is using one image across the whole virtual desktop.</summary>
    public bool IsSpanActive
    {
        get => _isSpanActive;
        set => SetSpan(value);
    }

    public bool PositionKnown { get; private set; } = true;

    public bool PositionFallbackVisible => !PositionKnown;

    public string PositionDescription => !PositionKnown
        ? "Fit mode could not be read from Windows; Fill is assumed, not confirmed."
        : "Each display remembers its own fit. Windows uses one global fit mode, which is set when you press Apply.";

    /// <summary>Binding source for the fit picker; changing it only edits the draft.</summary>
    public WallpaperPositionOption? SelectedPositionOption
    {
        get => _selectedPositionOption;
        set
        {
            if (ReferenceEquals(_selectedPositionOption, value))
                return;

            _selectedPositionOption = value;
            OnPropertyChanged(nameof(SelectedPositionOption));
            if (_suppressFitSync || value is null)
                return;

            SetFitForSelected(value.Fit);
        }
    }

    public string SelectedSourceKindName
    {
        get => _selectedSourceKindName;
        set
        {
            if (string.Equals(_selectedSourceKindName, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedSourceKindName = value ?? "File";
            OnPropertyChanged(nameof(SelectedSourceKindName));
            OnPropertyChanged(nameof(AvailableSources));
            OnPropertyChanged(nameof(IsFileSource));
            OnPropertyChanged(nameof(UsesListSource));
            SelectedSource = null;
            OnPropertyChanged(nameof(HasDraftSource));
            OnPropertyChanged(nameof(CanApply));
            ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
            Status = $"Source: {_selectedSourceKindName}. Choose a source, then press Apply.";
        }
    }

    public string DraftFilePath
    {
        get => _draftFilePath;
        set
        {
            if (!SetProperty(ref _draftFilePath, value ?? string.Empty, nameof(DraftFilePath)))
                return;

            OnPropertyChanged(nameof(HasDraftSource));
            OnPropertyChanged(nameof(CanApply));
            ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
        }
    }

    public SourceCandidate? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (ReferenceEquals(_selectedSource, value))
                return;

            _selectedSource = value;
            OnPropertyChanged(nameof(SelectedSource));
            OnPropertyChanged(nameof(HasDraftSource));
            OnPropertyChanged(nameof(CanApply));
            ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<SourceCandidate> AvailableSources =>
        string.Equals(_selectedSourceKindName, "Feed", StringComparison.OrdinalIgnoreCase)
            ? _feedSources
            : _librarySources;

    public bool HasDraftSource =>
        string.Equals(_selectedSourceKindName, "File", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(DraftFilePath)
            : SelectedSource?.HasImage == true;

    public bool IsFileSource =>
        string.Equals(_selectedSourceKindName, "File", StringComparison.OrdinalIgnoreCase);

    public bool UsesListSource => !IsFileSource;

    public string SpanImageName => string.IsNullOrWhiteSpace(_preferences.SpanImagePath)
        ? "No Span image chosen yet"
        : Path.GetFileName(_preferences.SpanImagePath);

    public string SpanPreviousFitLabel => $"{_preferences.SpanPreviousFit} fit";

    // Geometry for the monitor diagram. Values are DIPs inside the Canvas,
    // derived from DisplayMonitor.Left/Top/Width/Height at every refresh.
    public double LayoutWidth
    {
        get => _layoutWidth;
        private set => SetProperty(ref _layoutWidth, value, nameof(LayoutWidth));
    }

    public double LayoutHeight
    {
        get => _layoutHeight;
        private set => SetProperty(ref _layoutHeight, value, nameof(LayoutHeight));
    }

    public double DiagramScale
    {
        get => _diagramScale;
        private set => SetProperty(ref _diagramScale, value, nameof(DiagramScale));
    }

    public double OriginLeft
    {
        get => _originLeft;
        private set => SetProperty(ref _originLeft, value, nameof(OriginLeft));
    }

    public double OriginTop
    {
        get => _originTop;
        private set => SetProperty(ref _originTop, value, nameof(OriginTop));
    }

    public bool OriginVisible
    {
        get => _originVisible;
        private set => SetProperty(ref _originVisible, value, nameof(OriginVisible));
    }

    public bool NoMonitors
    {
        get => _noMonitors;
        private set => SetProperty(ref _noMonitors, value, nameof(NoMonitors));
    }

    /// <summary>Loads library/feed candidates after the page is attached.</summary>
    public async Task InitializeAsync()
    {
        var messages = new List<string>();
        IReadOnlyList<WallpaperLibraryEntry> entries;
        try
        {
            entries = await new WallpaperLibraryStore().GetAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            entries = Array.Empty<WallpaperLibraryEntry>();
            messages.Add($"Library sources unavailable: {ex.Message}");
        }

        _librarySources.Clear();
        _librarySources.AddRange(entries
            .Where(entry => IsUsablePath(entry.LocalPath))
            .Select(entry => new SourceCandidate(
                "Library",
                entry.Id,
                string.IsNullOrWhiteSpace(entry.Title) ? Path.GetFileName(entry.LocalPath) : entry.Title,
                entry.LocalPath,
                $"{entry.Width} x {entry.Height}",
                "Saved library image")));
        _librarySources.Sort((left, right) => string.CompareOrdinal(left.Label, right.Label));

        IReadOnlyList<RotationFeed> feeds;
        try
        {
            feeds = WallhavenFeedStore.ListFeeds();
        }
        catch (Exception ex)
        {
            feeds = Array.Empty<RotationFeed>();
            messages.Add($"Feed sources unavailable: {ex.Message}");
        }

        _feedSources.Clear();
        foreach (var feed in feeds)
        {
            var tag = $"feed:{feed.Id}";
            var newest = entries
                .Where(entry => IsUsablePath(entry.LocalPath)
                    && entry.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(entry => entry.SavedAtUtc)
                .FirstOrDefault();

            _feedSources.Add(new SourceCandidate(
                "Feed",
                feed.Id,
                feed.Name,
                newest?.LocalPath,
                newest is null ? null : $"{newest.Width} x {newest.Height}",
                newest is null
                    ? "No images fetched from this feed yet"
                    : $"Newest image from {feed.Name}"));
        }

        OnPropertyChanged(nameof(AvailableSources));
        OnPropertyChanged(nameof(HasDraftSource));
        OnPropertyChanged(nameof(CanApply));
        ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
        if (messages.Count > 0)
            Status = string.Join(" ", messages);
    }

    /// <summary>Code-behind calls this from the source-kind ComboBox.</summary>
    public void SetSourceKind(string? kind) => SelectedSourceKindName = kind ?? "File";

    /// <summary>Code-behind calls this from the candidate ComboBox/ListBox.</summary>
    public void SelectSource(SourceCandidate? candidate)
    {
        SelectedSource = candidate;
        if (candidate is null)
            return;

        Status = candidate.HasImage
            ? $"{candidate.Label} selected. Press Apply to put it on {SelectedMonitor?.DisplayName ?? "the target display"}."
            : $"{candidate.Label} has no fetched image yet. Fetch from Auto rotation first.";
    }

    /// <summary>Code-behind calls this after the file dialog returns a path.</summary>
    public void SetDraftFilePath(string? path)
    {
        DraftFilePath = path ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(DraftFilePath))
            Status = $"{Path.GetFileName(DraftFilePath)} selected. Press Apply to put it on {SelectedMonitor?.DisplayName ?? "the target display"}.";
    }

    /// <summary>Applies the staged source and fit to the target display.</summary>
    public void Apply()
    {
        var monitor = SelectedMonitor;
        if (monitor is null)
        {
            Status = "Select a display card first.";
            return;
        }

        if (IsSpanActive)
        {
            Status = "Span is active. Switch it off before applying a per-display image.";
            return;
        }

        string path;
        string label;
        string kind;
        string? sourceId;
        if (string.Equals(_selectedSourceKindName, "File", StringComparison.OrdinalIgnoreCase))
        {
            path = DraftFilePath;
            label = Path.GetFileName(path);
            kind = "File";
            sourceId = null;
        }
        else if (SelectedSource is { HasImage: true } candidate)
        {
            path = candidate.Path!;
            label = candidate.Label;
            kind = candidate.Kind;
            sourceId = candidate.Id;
        }
        else
        {
            Status = "Choose a file, library item or feed image first.";
            return;
        }

        PerformApply(monitor, path, label, kind, sourceId);
    }

    /// <summary>Applies a picked image to one display (kept for existing callers).</summary>
    public void ApplyWallpaper(DisplayCardViewModel monitor, string imagePath)
    {
        SetSelectedMonitor(monitor, persist: true);
        DraftFilePath = imagePath;
        SelectedSourceKindName = "File";
        PerformApply(monitor, imagePath, Path.GetFileName(imagePath), "File", null);
    }

    /// <summary>Applies one image across all displays in Span mode.</summary>
    public void ApplySpanWallpaper(string imagePath)
    {
        var previousFit = SelectedMonitor?.Fit ?? _preferences.SpanPreviousFit;
        var previousFingerprint = SelectedMonitor?.Fingerprint;
        string fullPath;
        try
        {
            fullPath = WindowsDesktopWallpaper.ValidateWallpaperPath(imagePath);
        }
        catch (Exception ex)
        {
            Status = $"Nothing changed: {ex.Message}";
            return;
        }

        try
        {
            WindowsDesktopWallpaper.ApplySpanWallpaper(fullPath);
            _preferences.SetSpan(true, previousFit, previousFingerprint, fullPath);
            _preferences.Save();
            _isSpanActive = true;
            OnPropertyChanged(nameof(IsSpanActive));
            OnPropertyChanged(nameof(SpanImageName));
            OnPropertyChanged(nameof(SpanPreviousFitLabel));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanChooseFit));
            ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ChooseImageCommand).RaiseCanExecuteChanged();
            ApplySpanPreviewToCards();
            Status = $"Span applied with {Path.GetFileName(fullPath)}. Switching Span off restores {previousFit} fit.";
        }
        catch (Exception ex)
        {
            TryRestorePosition(previousFit);
            Status = $"Nothing changed: the spanning wallpaper could not be applied. {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisplayDetection.DisplaysChanged -= OnDisplaysChanged;
        ChooseImageRequested = null;
        ChooseSpanImageRequested = null;
    }

    private void PerformApply(
        DisplayCardViewModel monitor,
        string imagePath,
        string sourceLabel,
        string sourceKind,
        string? sourceId)
    {
        var fingerprint = monitor.Fingerprint;
        var previousFit = _preferences.GetFit(fingerprint);
        string fullPath;
        try
        {
            fullPath = WindowsDesktopWallpaper.ValidateWallpaperPath(imagePath);
        }
        catch (Exception ex)
        {
            Status = $"Nothing changed for {monitor.DisplayName}: {ex.Message}";
            return;
        }

        try
        {
            // The OS fit mode is global. Applying this display's remembered fit
            // here keeps the visible result identical to the saved choice.
            WindowsDesktopWallpaper.ApplyWallpaper(monitor.Id, fullPath, monitor.Fit.ToWallpaperPosition());
            _preferences.SetFit(fingerprint, monitor.Fit);
            _preferences.SetImage(fingerprint, fullPath, sourceKind, sourceId);
            _preferences.SetSelection(fingerprint);
            _preferences.Save();

            monitor.SetImage(fullPath);
            PositionKnown = true;
            Status = $"Applied {sourceLabel} to {monitor.DisplayName} using {monitor.Fit} fit. Windows keeps one global fit mode; {monitor.DisplayName}'s choice is saved.";
        }
        catch (Exception ex)
        {
            TryRestorePosition(previousFit);
            Status = $"Nothing changed for {monitor.DisplayName}: {ex.Message}";
        }
    }

    private void TryRestorePosition(DisplayFit fit)
    {
        try
        {
            WindowsDesktopWallpaper.SetPosition(fit.ToWallpaperPosition());
        }
        catch (Exception)
        {
            // Best effort only: the original failure is the useful status.
        }
    }

    private void SetFitForSelected(DisplayFit fit)
    {
        var monitor = SelectedMonitor;
        if (monitor is null)
        {
            Status = "Select a display card before changing its fit.";
            SyncPositionSelection();
            return;
        }

        monitor.Fit = fit;
        _preferences.SetFit(monitor.Fingerprint, fit);
        _preferences.SetSelection(monitor.Fingerprint);
        _preferences.Save();
        Status = $"{monitor.DisplayName} fit set to {fit}. Press Apply to use it.";
    }

    private void SetSpan(bool active)
    {
        if (active == _isSpanActive)
            return;

        var fingerprint = SelectedMonitor?.Fingerprint;
        if (active)
        {
            var previousFit = SelectedMonitor?.Fit ?? _preferences.GetFit(fingerprint);
            try
            {
                WindowsDesktopWallpaper.SetPosition(DesktopWallpaperPosition.Span);
            }
            catch (Exception ex)
            {
                Status = $"Could not turn on Span: {ex.Message}";
                SyncSpanToggle();
                return;
            }

            _isSpanActive = true;
            _preferences.SetSpan(true, previousFit, fingerprint, _preferences.SpanImagePath);
            _preferences.Save();
            ApplySpanPreviewToCards();
            Status = $"Span is on. Windows uses one image across all displays. Switching it off restores {previousFit} fit.";
        }
        else
        {
            var restoreFit = _preferences.SpanPreviousFit;
            try
            {
                WindowsDesktopWallpaper.SetPosition(restoreFit.ToWallpaperPosition());
            }
            catch (Exception ex)
            {
                Status = $"Span could not be switched off: {ex.Message}";
                SyncSpanToggle();
                return;
            }

            _isSpanActive = false;
            _preferences.SetSpan(false, restoreFit, fingerprint, _preferences.SpanImagePath);
            _preferences.Save();
            // Re-read the per-monitor images the OS restores when Span exits.
            RefreshDisplays(preserveStatus: false);
            Status = $"Span is off. Restored {restoreFit} fit.";
        }

        OnPropertyChanged(nameof(IsSpanActive));
        OnPropertyChanged(nameof(SpanImageName));
        OnPropertyChanged(nameof(SpanPreviousFitLabel));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanChooseFit));
        ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ChooseImageCommand).RaiseCanExecuteChanged();
    }

    private void SyncSpanToggle() => OnPropertyChanged(nameof(IsSpanActive));

    private void ApplySpanPreviewToCards()
    {
        var bounds = CurrentVirtualBounds();
        var imagePath = _preferences.SpanImagePath ?? SelectedMonitor?.ImagePath;
        foreach (var monitor in Monitors)
        {
            monitor.SetImage(imagePath);
            monitor.SetSpanPreview(_isSpanActive, bounds);
        }
    }

    private (int Left, int Top, int Width, int Height)? CurrentVirtualBounds()
    {
        if (Monitors.Count == 0)
            return null;

        var left = Monitors.Min(monitor => monitor.Left);
        var top = Monitors.Min(monitor => monitor.Top);
        var right = Monitors.Max(monitor => monitor.Left + monitor.Width);
        var bottom = Monitors.Max(monitor => monitor.Top + monitor.Height);
        return (left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => RefreshDisplays(preserveStatus: true);

    private void SetSelectedMonitor(DisplayCardViewModel? monitor, bool persist)
    {
        if (_selectedMonitor is not null)
            _selectedMonitor.IsSelected = false;

        _selectedMonitor = monitor;
        if (monitor is not null)
            monitor.IsSelected = true;

        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanChooseFit));
        ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ChooseImageCommand).RaiseCanExecuteChanged();
        SyncPositionSelection();

        if (monitor is null)
            return;

        _preferences.SetSelection(monitor.Fingerprint);
        _preferences.Save();
        if (persist)
            MonitorSelectionService.Instance.Select(monitor.Id);
    }

    private void SyncPositionSelection()
    {
        var fit = SelectedMonitor?.Fit ?? _preferences.GetFit(null);
        var option = PositionOptions.FirstOrDefault(candidate => candidate.Fit == fit)
            ?? PositionOptions[0];

        _suppressFitSync = true;
        try
        {
            _selectedPositionOption = option;
            OnPropertyChanged(nameof(SelectedPositionOption));
        }
        finally
        {
            _suppressFitSync = false;
        }
    }

    private void RefreshDisplays() => RefreshDisplays(preserveStatus: false);

    private void RefreshDisplays(bool preserveStatus)
    {
        var previousFingerprint = SelectedMonitor?.Fingerprint ?? _preferences.SelectedFingerprint;
        Monitors.Clear();

        try
        {
            var detection = DisplayDetection.Read();
            if (!detection.Succeeded)
            {
                Status = $"Could not read display information: {detection.Error}";
                SetSelectedMonitor(null, persist: false);
                RefreshPosition();
                return;
            }

            var monitors = detection.Monitors;
            if (monitors.Count == 0)
            {
                Status = "Windows did not report any connected displays.";
                SetSelectedMonitor(null, persist: false);
                RefreshPosition();
                return;
            }

            foreach (var detected in monitors)
            {
                var fingerprint = DisplayDetection.Fingerprint(detected);
                var card = new DisplayCardViewModel(detected)
                {
                    Fit = _preferences.GetFit(fingerprint)
                };
                var imagePath = _preferences.GetImagePath(fingerprint) ?? detected.CurrentWallpaper;
                card.SetImage(imagePath);
                Monitors.Add(card);
            }

            // Resolve by stable panel fingerprint first: a hotplug changes the
            // COM path but not the physical monitor or its saved choices.
            MonitorSelectionService.Instance.Load(Monitors.Select(monitor => monitor.Id));
            var primary = Monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? Monitors[0];
            var rememberedId = MonitorSelectionService.Instance.Resolve(primary.Id);
            var target = Monitors.FirstOrDefault(monitor => monitor.Fingerprint == previousFingerprint)
                ?? Monitors.FirstOrDefault(monitor => monitor.Id == rememberedId)
                ?? primary;
            SetSelectedMonitor(target, persist: false);

            var positionKnown = RefreshPosition();
            if (_isSpanActive)
                ApplySpanPreviewToCards();

            var message = $"{Monitors.Count} display(s) detected. Selection and fit survive monitor changes; use one Apply for the chosen display.";
            Status = preserveStatus && !string.IsNullOrWhiteSpace(Status) ? $"{Status} {message}" : message;
            if (!positionKnown)
                Status += " Fit mode could not be read from Windows; Fill is assumed.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read display information: {ex.Message}";
        }
        finally
        {
            UpdateLayout();
        }
    }

    private bool RefreshPosition()
    {
        try
        {
            PositionKnown = true;
            OnPropertyChanged(nameof(PositionKnown));
            OnPropertyChanged(nameof(PositionFallbackVisible));
            return true;
        }
        catch (Exception)
        {
            PositionKnown = false;
            OnPropertyChanged(nameof(PositionKnown));
            OnPropertyChanged(nameof(PositionFallbackVisible));
            return false;
        }
    }

    /// <summary>
    /// Maps the virtual desktop onto the diagram. Cards keep a logical minimum
    /// size, then the whole diagram is scaled by LayoutTransform so it can never
    /// overflow its host; the outer ScrollViewer handles extreme layouts.
    /// </summary>
    private void UpdateLayout()
    {
        if (Monitors.Count == 0)
        {
            LayoutWidth = DiagramMaxWidth;
            LayoutHeight = 220;
            DiagramScale = 1;
            OriginVisible = false;
            NoMonitors = true;
            return;
        }

        NoMonitors = false;

        var left = Monitors.Min(monitor => monitor.Left);
        var top = Monitors.Min(monitor => monitor.Top);
        var right = Monitors.Max(monitor => monitor.Left + monitor.Width);
        var bottom = Monitors.Max(monitor => monitor.Top + monitor.Height);
        var virtualWidth = Math.Max(1d, (double)right - left);
        var virtualHeight = Math.Max(1d, (double)bottom - top);

        var fitScale = Math.Min(
            (DiagramMaxWidth - 2 * DiagramPadding) / virtualWidth,
            (DiagramMaxHeight - 2 * DiagramPadding) / virtualHeight);
        var logicalScale = Monitors.Max(monitor =>
            Math.Max(
                MinimumCardWidth / Math.Max(1d, monitor.Width),
                MinimumCardHeight / Math.Max(1d, monitor.Height)));
        var scale = Math.Max(logicalScale, 0.01d);
        var visualScale = Math.Clamp(fitScale / scale, 0.25d, 1d);

        foreach (var monitor in Monitors)
        {
            monitor.SetDiagramBounds(
                left: DiagramPadding + (monitor.Left - left) * scale,
                top: DiagramPadding + (monitor.Top - top) * scale,
                width: monitor.Width * scale,
                height: monitor.Height * scale);
        }

        LayoutWidth = virtualWidth * scale + 2 * DiagramPadding;
        LayoutHeight = virtualHeight * scale + 2 * DiagramPadding;
        DiagramScale = visualScale;
        OriginLeft = DiagramPadding - left * scale;
        OriginTop = DiagramPadding - top * scale;
        OriginVisible = left <= 0 && right >= 0 && top <= 0 && bottom >= 0;
    }

    private static bool IsUsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && WindowsDesktopWallpaper.IsSupportedImageExtension(Path.GetExtension(path));
}

/// <summary>The five real fit modes; Span is handled by its own toggle.</summary>
public static class DisplayFitMapper
{
    public static DesktopWallpaperPosition ToWallpaperPosition(this DisplayFit fit) => fit switch
    {
        DisplayFit.Fit => DesktopWallpaperPosition.Fit,
        DisplayFit.Stretch => DesktopWallpaperPosition.Stretch,
        DisplayFit.Center => DesktopWallpaperPosition.Center,
        DisplayFit.Tile => DesktopWallpaperPosition.Tile,
        _ => DesktopWallpaperPosition.Fill
    };

    public static DisplayFit ToDisplayFit(this DesktopWallpaperPosition position) => position switch
    {
        DesktopWallpaperPosition.Fit => DisplayFit.Fit,
        DesktopWallpaperPosition.Stretch => DisplayFit.Stretch,
        DesktopWallpaperPosition.Center => DisplayFit.Center,
        DesktopWallpaperPosition.Tile => DisplayFit.Tile,
        _ => DisplayFit.Fill
    };
}

/// <summary>One fit choice in the per-display fit picker.</summary>
public sealed class WallpaperPositionOption
{
    public WallpaperPositionOption(DisplayFit fit, string name)
    {
        Fit = fit;
        Name = name;
        Position = fit.ToWallpaperPosition();
    }

    /// <summary>Legacy constructor kept for callers that still pass a COM position.</summary>
    public WallpaperPositionOption(DesktopWallpaperPosition position, string name)
    {
        Position = position;
        Name = name;
        Fit = position.ToDisplayFit();
    }

    public DisplayFit Fit { get; }
    public DesktopWallpaperPosition Position { get; }
    public string Name { get; }
    public string Label => Name;

    public override string ToString() => Label;
}

/// <summary>One staged image source for the per-display picker.</summary>
public sealed class SourceKindOption
{
    public SourceKindOption(string name, string label)
    {
        Name = name;
        Label = label;
    }

    public string Name { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class SourceCandidate
{
    public SourceCandidate(
        string kind,
        string id,
        string label,
        string? path,
        string? resolutionText,
        string? detail)
    {
        Kind = kind;
        Id = id;
        Label = label;
        Path = path;
        ResolutionText = resolutionText;
        Detail = detail;
    }

    public string Kind { get; }
    public string Id { get; }
    public string Label { get; }
    public string? Path { get; }
    public string? ResolutionText { get; }
    public string? Detail { get; }
    public bool HasImage => !string.IsNullOrWhiteSpace(Path);

    public string Summary => HasImage
        ? string.IsNullOrWhiteSpace(ResolutionText) ? Label : $"{Label} - {ResolutionText}"
        : $"{Label} (no image yet)";

    public override string ToString() => Summary;
}

/// <summary>
/// Replaces the old six fit booleans. One converter maps the fit enum (plus the
/// Span flag) onto the ImageBrush properties that produce the real crop/fill.
/// </summary>
public sealed class DisplayFitPreviewConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var fit = values.Length > 0 && values[0] is DisplayFit parsed ? parsed : DisplayFit.Fill;
        var span = values.Length > 1 && values[1] is bool flag && flag;
        return ConvertCore(fit, span, parameter as string, targetType);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        new object[] { Binding.DoNothing };

    private static object ConvertCore(DisplayFit fit, bool span, string? parameter, Type targetType)
    {
        if (span)
        {
            return parameter switch
            {
                "Stretch" when targetType == typeof(Stretch) => Stretch.Fill,
                "TileMode" when targetType == typeof(TileMode) => TileMode.None,
                "ViewportUnits" when targetType == typeof(BrushMappingMode) => BrushMappingMode.RelativeToBoundingBox,
                "AlignmentX" when targetType == typeof(AlignmentX) => AlignmentX.Left,
                "AlignmentY" when targetType == typeof(AlignmentY) => AlignmentY.Top,
                _ => Fallback(parameter, targetType)
            };
        }

        return parameter switch
        {
            "Stretch" when targetType == typeof(Stretch) => fit switch
            {
                DisplayFit.Fill => Stretch.UniformToFill,
                DisplayFit.Fit => Stretch.Uniform,
                DisplayFit.Stretch => Stretch.Fill,
                _ => Stretch.None
            },
            "TileMode" when targetType == typeof(TileMode) =>
                fit == DisplayFit.Tile ? TileMode.Tile : TileMode.None,
            "ViewportUnits" when targetType == typeof(BrushMappingMode) =>
                fit == DisplayFit.Tile ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox,
            "AlignmentX" when targetType == typeof(AlignmentX) => AlignmentX.Center,
            "AlignmentY" when targetType == typeof(AlignmentY) => AlignmentY.Center,
            _ => Fallback(parameter, targetType)
        };
    }

    private static object Fallback(string? parameter, Type targetType)
    {
        if (targetType == typeof(Stretch))
            return Stretch.UniformToFill;
        if (targetType == typeof(TileMode))
            return TileMode.None;
        if (targetType == typeof(BrushMappingMode))
            return BrushMappingMode.RelativeToBoundingBox;
        if (targetType == typeof(AlignmentX))
            return AlignmentX.Center;
        if (targetType == typeof(AlignmentY))
            return AlignmentY.Center;
        return Binding.DoNothing;
    }
}

/// <summary>State for one connected monitor, including its real image preview.</summary>
public sealed class DisplayCardViewModel : ViewModelBase
{
    private DisplayFit _fit = DisplayFit.Fill;
    private ImageSource? _previewSource;
    private string _imagePath = string.Empty;
    private string _imageResolutionText = "Resolution unavailable";
    private string _imageStatusText = "No image reported by Windows";
    private bool _isSpanPreview;
    private Rect _previewViewport = new(0, 0, 1, 1);
    private Rect _previewViewbox = new(0, 0, 1, 1);
    private (int Left, int Top, int Width, int Height)? _spanBounds;
    private Size? _imagePixelSize;
    private bool _isSelected;
    private double _diagramLeft;
    private double _diagramTop;
    private double _diagramWidth;
    private double _diagramHeight;

    public DisplayCardViewModel(DisplayMonitor monitor, int index = 0)
    {
        Id = monitor.Id;
        DeviceName = monitor.DeviceName;
        DisplayName = monitor.DisplayNumberText;
        IsPrimary = monitor.IsPrimary;
        Left = monitor.Left;
        Top = monitor.Top;
        Width = monitor.Width;
        Height = monitor.Height;
        CurrentWallpaper = monitor.CurrentWallpaper;
        Fingerprint = DisplayDetection.Fingerprint(monitor);
        _imagePath = monitor.CurrentWallpaper ?? string.Empty;
    }

    public string Id { get; }
    public string DeviceName { get; }
    public string Fingerprint { get; }
    public string DisplayName { get; }
    public bool IsPrimary { get; }
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public string? CurrentWallpaper { get; }
    public string ImagePath => _imagePath;

    public DisplayFit Fit
    {
        get => _fit;
        internal set
        {
            if (!SetProperty(ref _fit, value, nameof(Fit)))
                return;

            OnPropertyChanged(nameof(FitLabel));
            UpdatePreviewGeometry();
        }
    }

    public string FitLabel => Fit.ToString();

    /// <summary>True when the preview must show this monitor's slice of a spanned image.</summary>
    public bool IsSpanPreview
    {
        get => _isSpanPreview;
        private set => SetProperty(ref _isSpanPreview, value, nameof(IsSpanPreview));
    }

    public ImageSource? PreviewSource
    {
        get => _previewSource;
        private set => SetProperty(ref _previewSource, value, nameof(PreviewSource));
    }

    public string ImageResolutionText
    {
        get => _imageResolutionText;
        private set => SetProperty(ref _imageResolutionText, value, nameof(ImageResolutionText));
    }

    public string ImageStatusText
    {
        get => _imageStatusText;
        private set => SetProperty(ref _imageStatusText, value, nameof(ImageStatusText));
    }

    public Rect PreviewViewport
    {
        get => _previewViewport;
        private set => SetProperty(ref _previewViewport, value, nameof(PreviewViewport));
    }

    public Rect PreviewViewbox
    {
        get => _previewViewbox;
        private set => SetProperty(ref _previewViewbox, value, nameof(PreviewViewbox));
    }

    /// <summary>Canvas bounds for the scaled layout diagram.</summary>
    public double DiagramLeft => _diagramLeft;
    public double DiagramTop => _diagramTop;
    public double DiagramWidth => _diagramWidth;
    public double DiagramHeight => _diagramHeight;

    internal void SetDiagramBounds(double left, double top, double width, double height)
    {
        SetProperty(ref _diagramLeft, left, nameof(DiagramLeft));
        SetProperty(ref _diagramTop, top, nameof(DiagramTop));
        SetProperty(ref _diagramWidth, width, nameof(DiagramWidth));
        SetProperty(ref _diagramHeight, height, nameof(DiagramHeight));
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public string ResolutionText => $"{Width:N0} x {Height:N0}  |  Position {Left}, {Top}";

    /// <summary>The file name of the image actually shown in the preview.</summary>
    public string ImageName => string.IsNullOrWhiteSpace(_imagePath)
        ? "No image available"
        : Path.GetFileName(_imagePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string WallpaperName => string.IsNullOrWhiteSpace(CurrentWallpaper)
        ? "No wallpaper reported by Windows"
        : Path.GetFileName(CurrentWallpaper.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string WallpaperPathToolTip => string.IsNullOrWhiteSpace(_imagePath)
        ? "Windows did not report a wallpaper path for this display."
        : _imagePath;

    public string ChooseImageAutomationName => $"Choose wallpaper for {DisplayName} ({DeviceName})";

    /// <summary>Loads the real image and its pixel resolution; never throws.</summary>
    internal void SetImage(string? path)
    {
        _imagePath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        _imagePixelSize = null;
        PreviewSource = null;
        ImageResolutionText = "Resolution unavailable";
        ImageStatusText = string.IsNullOrWhiteSpace(_imagePath)
            ? "No image reported by Windows"
            : string.Empty;
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(ImageName));
        OnPropertyChanged(nameof(WallpaperPathToolTip));

        if (!string.IsNullOrWhiteSpace(_imagePath))
        {
            try
            {
                var fullPath = WindowsDesktopWallpaper.ValidateWallpaperPath(_imagePath);
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(fullPath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                _imagePixelSize = new Size(image.PixelWidth, image.PixelHeight);
                PreviewSource = image;
                ImageResolutionText = $"{image.PixelWidth:N0} x {image.PixelHeight:N0}";
            }
            catch (Exception ex)
            {
                ImageStatusText = $"Preview unavailable: {ex.Message}";
            }
        }

        UpdatePreviewGeometry();
    }

    /// <summary>Span needs a slice of one image across the whole virtual desktop.</summary>
    internal void SetSpanPreview(bool active, (int Left, int Top, int Width, int Height)? bounds)
    {
        IsSpanPreview = active;
        _spanBounds = bounds;
        UpdatePreviewGeometry();
    }

    private void UpdatePreviewGeometry()
    {
        if (_isSpanPreview
            && _spanBounds is { } desktop
            && desktop.Width > 0
            && desktop.Height > 0
            && _imagePixelSize is { Width: > 0, Height: > 0 } pixels)
        {
            // Windows spans by filling the whole virtual desktop, cropping the
            // longer image axis and slicing that result per monitor.
            var desktopAspect = desktop.Width / (double)desktop.Height;
            var imageAspect = pixels.Width / pixels.Height;
            double visibleWidth;
            double visibleHeight;
            double offsetX;
            double offsetY;
            if (imageAspect > desktopAspect)
            {
                visibleHeight = pixels.Height;
                visibleWidth = visibleHeight * desktopAspect;
                offsetX = (pixels.Width - visibleWidth) / 2d;
                offsetY = 0;
            }
            else
            {
                visibleWidth = pixels.Width;
                visibleHeight = visibleWidth / desktopAspect;
                offsetX = 0;
                offsetY = (pixels.Height - visibleHeight) / 2d;
            }

            var sourceX = (offsetX + (Left - desktop.Left) * (visibleWidth / desktop.Width)) / pixels.Width;
            var sourceY = (offsetY + (Top - desktop.Top) * (visibleHeight / desktop.Height)) / pixels.Height;
            var sourceWidth = Width * (visibleWidth / desktop.Width) / pixels.Width;
            var sourceHeight = Height * (visibleHeight / desktop.Height) / pixels.Height;
            PreviewViewbox = new Rect(
                Math.Clamp(sourceX, 0, 1),
                Math.Clamp(sourceY, 0, 1),
                Math.Clamp(sourceWidth, 0, 1),
                Math.Clamp(sourceHeight, 0, 1));
            PreviewViewport = new Rect(0, 0, 1, 1);
            return;
        }

        PreviewViewbox = new Rect(0, 0, 1, 1);
        PreviewViewport = Fit == DisplayFit.Tile && _imagePixelSize is { Width: > 0, Height: > 0 } tile
            ? new Rect(0, 0, tile.Width, tile.Height)
            : new Rect(0, 0, 1, 1);
    }
}
