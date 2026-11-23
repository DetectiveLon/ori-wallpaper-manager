namespace Oriole.Core.Services;

/// <summary>
/// Sequential, one-shot rotation loop. It never queues timer ticks while a download or
/// wallpaper apply is in progress. The next interval starts after the previous attempt ends.
/// A pause that lasts past a due time defers the change and starts a fresh interval on resume.
/// </summary>
public sealed class RotationLoop(TimeProvider? timeProvider = null)
{
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

        if (lastCompletedAt is null)
            return now + interval;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shouldPause);
        ArgumentNullException.ThrowIfNull(rotateAsync);
        if (interval < TimeSpan.FromMinutes(1) || interval > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(interval));

        while (_resetSignal.Wait(0))
        {
        }

        var nextRun = _timeProvider.GetUtcNow() + interval;
        if (nextRunChanged is not null)
            await nextRunChanged(nextRun).ConfigureAwait(false);

        var deferredByPause = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = nextRun - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(remaining, _timeProvider, waitCancellation.Token);
                var resetTask = _resetSignal.WaitAsync(waitCancellation.Token);
                await Task.WhenAny(delayTask, resetTask).ConfigureAwait(false);

                if (resetTask.IsCompletedSuccessfully)
                {
                    await resetTask.ConfigureAwait(false);
                    waitCancellation.Cancel();
                    nextRun = _timeProvider.GetUtcNow() + interval;
                    if (nextRunChanged is not null)
                        await nextRunChanged(nextRun).ConfigureAwait(false);
                    continue;
                }

                waitCancellation.Cancel();
                await delayTask.ConfigureAwait(false);
            }

            if (_resetSignal.Wait(0))
            {
                nextRun = _timeProvider.GetUtcNow() + interval;
                if (nextRunChanged is not null)
                    await nextRunChanged(nextRun).ConfigureAwait(false);
                continue;
            }

            while (shouldPause())
            {
                deferredByPause = true;
                await Task.Delay(TimeSpan.FromSeconds(15), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (deferredByPause)
            {
                // Do not apply immediately after a long pause (for example, after a game exits).
                nextRun = _timeProvider.GetUtcNow() + interval;
                deferredByPause = false;
                if (nextRunChanged is not null)
                    await nextRunChanged(nextRun).ConfigureAwait(false);
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

            nextRun = _timeProvider.GetUtcNow() + interval;
            if (nextRunChanged is not null)
                await nextRunChanged(nextRun).ConfigureAwait(false);
        }
    }
}
