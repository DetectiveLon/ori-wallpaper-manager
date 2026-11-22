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
    [InlineData(7)]
    public void InvalidIntervalsAreRejected(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RotationLoop.CalculateNextRun(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes), null));
    }
}
