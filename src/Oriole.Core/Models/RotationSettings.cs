namespace Oriole.Core.Models;

public sealed record RotationSettings
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(30);
    public bool PauseDuringFullscreenApps { get; init; } = true;
    public bool PauseOnBattery { get; init; }

    public void Validate()
    {
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(Interval), "Rotation interval must be between 1 minute and 7 days.");
    }
}
