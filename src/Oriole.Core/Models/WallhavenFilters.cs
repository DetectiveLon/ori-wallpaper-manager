namespace Oriole.Core.Models;

[Flags]
public enum WallhavenCategory
{
    None = 0,
    General = 1,
    Anime = 2,
    People = 4,
    All = General | Anime | People
}

[Flags]
public enum WallhavenPurity
{
    None = 0,
    Safe = 1,
    Sketchy = 2,
    Explicit = 4,
    All = Safe | Sketchy | Explicit
}

public enum WallhavenSort
{
    DateAdded,
    Relevance,
    Random,
    Views,
    Favorites,
    Toplist
}

public enum WallhavenOrder
{
    Descending,
    Ascending
}

public enum WallhavenTopRange
{
    Day,
    ThreeDays,
    Week,
    Month,
    ThreeMonths,
    SixMonths,
    Year
}

/// <summary>All supported search criteria for the Wallhaven v1 search endpoint.</summary>
public sealed record WallhavenFilters
{
    public string Query { get; init; } = string.Empty;
    public WallhavenCategory Categories { get; init; } = WallhavenCategory.All;
    public WallhavenPurity Purity { get; init; } = WallhavenPurity.Safe;
    public WallhavenSort Sorting { get; init; } = WallhavenSort.Random;
    public WallhavenOrder Order { get; init; } = WallhavenOrder.Descending;
    public WallhavenTopRange TopRange { get; init; } = WallhavenTopRange.Month;
    public string? MinimumResolution { get; init; }
    public string? ExactResolution { get; init; }
    public string? Ratio { get; init; }
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
    public int Page { get; init; } = 1;
    public string? Seed { get; init; }

    public void Validate()
    {
        if (Categories == WallhavenCategory.None || (Categories & ~WallhavenCategory.All) != 0)
            throw new ArgumentException("Choose at least one category.", nameof(Categories));

        if (Purity == WallhavenPurity.None || (Purity & ~WallhavenPurity.All) != 0)
            throw new ArgumentException("Choose at least one content level.", nameof(Purity));

        if (Page < 1)
            throw new ArgumentOutOfRangeException(nameof(Page), "Page must be 1 or greater.");

        ValidateResolution(MinimumResolution, nameof(MinimumResolution));
        ValidateResolution(ExactResolution, nameof(ExactResolution));
        if (!string.IsNullOrWhiteSpace(MinimumResolution) && !string.IsNullOrWhiteSpace(ExactResolution))
            throw new ArgumentException("Choose either a minimum resolution or one exact resolution, not both.");

        if (!string.IsNullOrWhiteSpace(Ratio)
            && !System.Text.RegularExpressions.Regex.IsMatch(Ratio, @"^\d{1,3}x\d{1,3}$"))
            throw new ArgumentException($"Invalid aspect ratio '{Ratio}'. Use a value such as 16x9.", nameof(Ratio));

        foreach (var color in Colors)
        {
            if (!WallhavenColors.Allowed.Contains(color.Trim().TrimStart('#')))
                throw new ArgumentException($"Unsupported Wallhaven color '{color}'.", nameof(Colors));
        }
    }

    private static void ValidateResolution(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{3,5}x\d{3,5}$"))
            throw new ArgumentException($"Invalid resolution '{value}'. Use a value such as 1920x1080.", parameterName);
    }
}
