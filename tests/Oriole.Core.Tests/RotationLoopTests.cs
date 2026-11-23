using Oriole.Core.Services;

namespace Oriole.Core.Tests;

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
