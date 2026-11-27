using Ori.Core.Services;

namespace Ori.Core.Tests;

public sealed class RotationLoopTests
{
    [Fact]
    public void FirstRunIsOneFullIntervalFromNow()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddMinutes(30), RotationLoop.CalculateNextRun(now, TimeSpan.FromMinutes(30), null));
    }

    [Fact]
    public void CompletedRunUsesCompletionTimeAsIntervalAnchor()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 30, 0, TimeSpan.Zero);
        var completed = now.AddMinutes(-5);
        Assert.Equal(now.AddMinutes(25), RotationLoop.CalculateNextRun(now, TimeSpan.FromMinutes(30), completed));
    }

    [Fact]
    public void MissedRunDoesNotCauseImmediateCatchUpBurst()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 30, 0, TimeSpan.Zero);
        var completed = now.AddHours(-2);
        Assert.Equal(now.AddMinutes(30), RotationLoop.CalculateNextRun(now, TimeSpan.FromMinutes(30), completed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10081)]
    public void InvalidIntervalsAreRejected(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RotationLoop.CalculateNextRun(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes), null));
    }
    [Fact]
    public async Task ManualResetMovesDeadlineWithoutRunningARotation()
    {
        var loop = new RotationLoop();
        using var cancellation = new CancellationTokenSource();
        var initialDeadline = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetDeadline = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var rotationCount = 0;

        var run = loop.RunAsync(
            TimeSpan.FromHours(1),
            shouldPause: () => false,
            rotateAsync: _ =>
            {
                Interlocked.Increment(ref rotationCount);
                return Task.CompletedTask;
            },
            nextRunChanged: nextRun =>
            {
                if (Interlocked.Increment(ref callbackCount) == 1)
                    initialDeadline.TrySetResult(nextRun);
                else
                    resetDeadline.TrySetResult(nextRun);
                return Task.CompletedTask;
            },
            cancellationToken: cancellation.Token);

        var initial = await initialDeadline.Task.WaitAsync(TimeSpan.FromSeconds(2));
        loop.ResetNextRun();
        var reset = await resetDeadline.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(reset > initial);
        Assert.Equal(0, rotationCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ThrowingRotateAndThrowingFailedCallbackStillRotateNextInterval()
    {
        var rotations = 0;
        var failures = 0;
        var secondFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold the loop at the second failure so the counters are stable while the
        // test reads them. Virtual time otherwise spins through many more intervals
        // before the assertion runs, which made an equality check racy.
        var releaseSecondFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var loop = new RotationLoop(new AdvancingTimeProvider(TimeSpan.FromMinutes(1)));

        var run = loop.RunAsync(
            TimeSpan.FromMinutes(1),
            shouldPause: () => false,
            rotateAsync: _ =>
            {
                Interlocked.Increment(ref rotations);
                throw new InvalidOperationException("rotation failed");
            },
            failed: _ =>
            {
                if (Interlocked.Increment(ref failures) == 2)
                {
                    secondFailure.TrySetResult();
                    return releaseSecondFailure.Task;
                }
                throw new InvalidOperationException("reporting failed");
            },
            cancellationToken: cancellation.Token);

        await secondFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, rotations);
        Assert.Equal(2, failures);

        releaseSecondFailure.SetResult();
        cancellation.Cancel();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the loop's normal exit path.
        }
    }

    /// <summary>
    /// A deterministic clock: each wait advances virtual time by one interval and
    /// fires the delay immediately, so a one-minute schedule needs no real waiting.
    /// </summary>
    private sealed class AdvancingTimeProvider(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero)
                _now += step;
            if (dueTime != Timeout.InfiniteTimeSpan)
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ResetDuringPauseAppliesFreshIntervalExactlyOnce()
    {
        var loop = new RotationLoop();
        using var cancellation = new CancellationTokenSource();
        var paused = false;
        var notifications = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = loop.RunAsync(
            TimeSpan.FromMinutes(30),
            shouldPause: () => Volatile.Read(ref paused),
            rotateAsync: _ => Task.CompletedTask,
            nextRunChanged: _ =>
            {
                if (Interlocked.Increment(ref notifications) == 1)
                    first.TrySetResult();
                return Task.CompletedTask;
            },
            cancellationToken: cancellation.Token);

        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref paused, true);
        await Task.Delay(TimeSpan.FromMilliseconds(2200)); // let the loop enter its pause poll
        loop.ResetNextRun();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Volatile.Write(ref paused, false);
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(2, notifications);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
