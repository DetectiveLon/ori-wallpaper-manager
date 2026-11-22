namespace Oriole.Core.Services;

/// <summary>
/// Sequential, one-shot rotation loop. It never queues timer ticks while a download or
/// wallpaper apply is in progress. The next interval starts after the previous attempt ends.
/// </summary>
public sealed class RotationLoop(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        var nextRun = _timeProvider.GetUtcNow() + interval;
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = nextRun - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);

            while (shouldPause())
                await Task.Delay(TimeSpan.FromSeconds(15), _timeProvider, cancellationToken).ConfigureAwait(false);

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
