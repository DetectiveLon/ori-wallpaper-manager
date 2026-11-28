using Ori.Core.Models;
using Ori.Core.Services;
using Ori.Desktop.ViewModels;

namespace Ori.Desktop.Services;

/// <summary>
/// One rotation session per connected display. Owns the per-display
/// <see cref="RotationLoop"/>, its cancellation, an explicit <see cref="RotationState"/>,
/// the next-run timestamp, source selection and pause flags. The view model is only a
/// binding surface over a collection of these sessions.
/// </summary>
/// <remarks>
/// Engine state lives here, not on the per-card view model. <see cref="RotationLoop"/>
/// itself is untouched. All mutable fields are safe to read from rotation callbacks;
/// callers marshal UI updates back to the dispatcher.
/// </remarks>
public sealed class RotationSession : IDisposable
{
    public RotationSession(DisplayMonitor monitor, string intervalName = "30 minutes")
    {
        Monitor = monitor;
        IntervalName = string.IsNullOrWhiteSpace(intervalName) ? "30 minutes" : intervalName;
        Fingerprint = ScheduleStore.BuildFingerprint(
            monitor.DeviceName, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
    }

    /// <summary>Latest Windows snapshot for this panel; replaced on hotplug refresh.</summary>
    public DisplayMonitor Monitor { get; set; }

    /// <summary>Stable identity used to adopt a saved schedule after re-enumeration.</summary>
    public string Fingerprint { get; private set; }

    /// <summary>Owned, untouched rotation loop.</summary>
    public RotationLoop Loop { get; } = new();

    /// <summary>Cancels the running loop. Created on start, disposed on stop.</summary>
    public CancellationTokenSource? LoopCancellation { get; set; }

    /// <summary>Cancels an in-flight manual rotation (Rotate now / Previous / Fetch).</summary>
    public CancellationTokenSource? ManualCancellation { get; set; }

    public Task? LoopTask { get; set; }
    public Task? LoopObserverTask { get; set; }
    public Task? ManualTask { get; set; }

    /// <summary>Explicit lifecycle state, independent of localized status text.</summary>
    public RotationState State { get; set; } = RotationState.Idle;

    /// <summary>Next scheduled change (UTC); null when not counting down.</summary>
    public DateTimeOffset? NextRunUtc { get; set; }

    /// <summary>Persisted interval name; arbitrary whole minutes 1..10080 or a legacy name.</summary>
    public string IntervalName { get; set; }

    // ---- pause flags ----
    public bool UserPaused { get; set; }
    public bool FullscreenPaused { get; set; }
    /// <summary>Per-display opt-out; effective only when the app-wide master is also on.</summary>
    public bool PauseWhenFullscreen { get; set; } = true;

    // ---- source selection ----
    public RotationSourceKind SourceKind { get; set; } = RotationSourceKind.Library;
    public string? SourceId { get; set; }

    // ---- bookkeeping ----
    public bool ActiveSchedule { get; set; }
    public bool IsStarting { get; set; }
    public bool ManualRotationRunning { get; set; }
    public bool PreviousRotationRunning { get; set; }
    public int ManualRotationGeneration;
    public string? LastAppliedId { get; set; }
    public string? LastAppliedPath { get; set; }
    public string? PreviousAppliedPath { get; set; }
    public string? PendingFeedEntryId { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>True while the loop task is still running.</summary>
    public bool IsRunning => LoopTask is { IsCompleted: false };

    /// <summary>Resolved interval; falls back to 30 minutes for an unparseable name.</summary>
    public TimeSpan Interval =>
        ScheduleStore.TryParseIntervalName(IntervalName, out var minutes)
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(30);

    /// <summary>Refreshes <see cref="Fingerprint"/> after a geometry change.</summary>
    public void RefreshFingerprint() => Fingerprint = ScheduleStore.BuildFingerprint(
        Monitor.DeviceName, Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height);

    /// <summary>Cancels loop + manual cancellation without awaiting unwind.</summary>
    public void CancelLoops()
    {
        try { LoopCancellation?.Cancel(); } catch { /* already disposed */ }
        try { ManualCancellation?.Cancel(); } catch { /* already disposed */ }
    }

    public void Dispose()
    {
        CancelLoops();
        try { LoopCancellation?.Dispose(); } catch { /* already disposed */ }
        try { ManualCancellation?.Dispose(); } catch { /* already disposed */ }
        LoopCancellation = null;
        ManualCancellation = null;
    }
}
