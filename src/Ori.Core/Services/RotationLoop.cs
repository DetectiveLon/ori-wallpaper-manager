namespace Ori.Core.Services;

/// <summary>
/// Sequential, one-shot rotation loop. It never queues timer ticks while a wallpaper change
/// is in progress. Pause is polled during the countdown; once resumed, a fresh full interval
/// starts instead of applying immediately or replaying missed intervals.
/// </summary>
public sealed class RotationLoop(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan PausePollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _resetSignal = new(0, 1);

    /// <summary>Restarts the pending interval after a successful manual rotation.</summary>
    public void ResetNextRun()
    {
        try
        {
            _resetSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Multiple manual requests collapse into one reset instead of building a queue.
        }
    }

    public static DateTimeOffset CalculateNextRun(
        DateTimeOffset now,
        TimeSpan interval,
        DateTimeOffset? lastCompletedAt)
    {
        if (interval < TimeSpan.FromMinutes(1) || interval > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(interval));

        if (lastCompletedAt is null || lastCompletedAt.Value > now)
            return now + interval; // A completion timestamp from the future is stale clock state.

        var candidate = lastCompletedAt.Value + interval;
        // Missed intervals are skipped, never replayed in a burst after sleep or shutdown.
        return candidate > now ? candidate : now + interval;
    }

    public async Task RunAsync(
        TimeSpan interval,
        Func<bool> shouldPause,
        Func<CancellationToken, Task> rotateAsync,
        Func<DateTimeOffset, Task>? nextRunChanged = null,
        Func<Exception, Task>? failed = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastCompletedAt = null,
        Func<TimeSpan>? intervalProvider = null)
    {
        ArgumentNullException.ThrowIfNull(shouldPause);
        ArgumentNullException.ThrowIfNull(rotateAsync);
        if (interval < TimeSpan.FromMinutes(1) || interval > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(interval));

        // A live provider lets a committed custom interval take effect on a running
        // schedule without Stop/Start. Out-of-range values fall back to the validated
        // start interval so a bad edit can never produce a busy or stalled loop.
        TimeSpan EffectiveInterval()
        {
            var candidate = intervalProvider?.Invoke() ?? interval;
            return candidate >= TimeSpan.FromMinutes(1) && candidate <= TimeSpan.FromDays(7)
                ? candidate
                : interval;
        }

        // A throwing UI callback must not unwind the loop. Keep the original delegates
        // in locals so the wrappers cannot recurse into themselves.
        var notify = nextRunChanged;
        if (notify is not null)
            nextRunChanged = async nextRun =>
            {
                try
                {
                    await notify(nextRun).ConfigureAwait(false);
                }
                catch
                {
                    // Reporting failures must never stop future rotations.
                }
            };

        var report = failed;
        if (report is not null)
            failed = async exception =>
            {
                try
                {
                    await report(exception).ConfigureAwait(false);
                }
                catch
                {
                }
            };

        while (_resetSignal.Wait(0))
        {
        }

        var currentInterval = EffectiveInterval();
        var nextRun = CalculateNextRun(_timeProvider.GetUtcNow(), currentInterval, lastCompletedAt);
        if (nextRunChanged is not null)
            await nextRunChanged(nextRun).ConfigureAwait(false);

        var deferredByPause = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var intervalNow = EffectiveInterval();
            if (intervalNow != currentInterval)
            {
                // The user committed a new interval while this loop was running:
                // re-anchor immediately so the countdown and the UI agree.
                currentInterval = intervalNow;
                nextRun = _timeProvider.GetUtcNow() + currentInterval;
                if (nextRunChanged is not null)
                    await nextRunChanged(nextRun).ConfigureAwait(false);
            }

            if (shouldPause())
            {
                deferredByPause = true;
                await Task.Delay(PausePollInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (deferredByPause)
            {
                // Discard resets issued while paused: a resumed schedule always starts a
                // fresh full interval, and a leftover permit would reset it a second time.
                while (_resetSignal.Wait(0))
                {
                }

                // Do not apply immediately after a pause (for example, after a game exits).
                nextRun = _timeProvider.GetUtcNow() + currentInterval;
                deferredByPause = false;
                if (nextRunChanged is not null)
                    await nextRunChanged(nextRun).ConfigureAwait(false);
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            var remaining = nextRun - now;
            if (remaining > currentInterval)
            {
                // The clock jumped backwards: a deadline computed before the jump
                // must not stretch this interval.
                nextRun = now + currentInterval;
                remaining = currentInterval;
                if (nextRunChanged is not null)
                    await nextRunChanged(nextRun).ConfigureAwait(false);
            }
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero; // A forward jump must not produce a negative wait.

            if (remaining > TimeSpan.Zero)
            {
                var waitDuration = remaining < PausePollInterval
                    ? remaining
                    : PausePollInterval;

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(waitDuration, _timeProvider, waitCancellation.Token);
                var resetTask = _resetSignal.WaitAsync(waitCancellation.Token);
                await Task.WhenAny(delayTask, resetTask).ConfigureAwait(false);

                if (resetTask.IsCompletedSuccessfully)
                {
                    await resetTask.ConfigureAwait(false);
                    waitCancellation.Cancel();
                    nextRun = _timeProvider.GetUtcNow() + currentInterval;
                    if (nextRunChanged is not null)
                        await nextRunChanged(nextRun).ConfigureAwait(false);
                }
                else
                {
                    waitCancellation.Cancel();
                    await delayTask.ConfigureAwait(false);
                }

                continue;
            }

            if (_resetSignal.Wait(0))
            {
                nextRun = _timeProvider.GetUtcNow() + currentInterval;
                if (nextRunChanged is not null)
                    await nextRunChanged(nextRun).ConfigureAwait(false);
                continue;
            }

            // Pause can begin between the last poll and the deadline check.
            if (shouldPause())
            {
                deferredByPause = true;
                continue;
            }

            try
            {
                await rotateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (failed is not null)
                    await failed(ex).ConfigureAwait(false);
            }

            nextRun = _timeProvider.GetUtcNow() + currentInterval;
            if (nextRunChanged is not null)
                await nextRunChanged(nextRun).ConfigureAwait(false);
        }
    }
}
