namespace Ori.Core.Models;

/// <summary>
/// One saved Wallhaven search in the hands-free rotation list. The record carries
/// its own cadence and failure state so feeds can be scheduled independently.
/// It is a plain data model: no file access, no UI dependencies.
/// </summary>
public sealed record RotationFeed
{
    internal const int MaximumIdLength = 64;
    internal const int MaximumNameLength = 80;
    internal const int MaximumAppliedIdLength = 40;
    internal const int MaximumFailureCount = 20;
    internal const int MinimumCadence = 1;
    internal const int MaximumCadence = 7 * 24 * 60;

    /// <summary>Paging never asks Wallhaven for fewer than one item.</summary>
    public const int MinimumMaximumItemsPerFetch = 1;
    /// <summary>Wallhaven's per-page cap; fetched items per page are clamped to this.</summary>
    public const int MaximumMaximumItemsPerFetch = 10;
    /// <summary>How many new items one fetch contributes by default.</summary>
    public const int DefaultMaximumItemsPerFetch = 3;
    /// <summary>First page in Wallhaven's 1-based paging.</summary>
    public const int FirstPage = 1;
    /// <summary>Persisted seeds are bounded so a corrupt record cannot grow the file.</summary>
    public const int MaximumSeedLength = 64;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public WallhavenFilters Filters { get; init; } = new();
    public int CadenceMinutes { get; init; } = 60;
    public bool Enabled { get; init; } = true;
    public DateTimeOffset? LastFetchedUtc { get; init; }
    public DateTimeOffset? BackoffUntilUtc { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? LastAppliedId { get; init; }

    // ----- paging state (schema v3) -----

    /// <summary>1-based Wallhaven page this feed fetches next. Always at least 1.</summary>
    public int CurrentPage
    {
        get => _currentPage;
        init => _currentPage = value < FirstPage ? FirstPage : value;
    }
    private int _currentPage = FirstPage;

    /// <summary>Last page Wallhaven reported for this feed's query, when known.</summary>
    public int? LastPage
    {
        get => _lastPage;
        init => _lastPage = value is { } page && page >= FirstPage ? page : null;
    }
    private int? _lastPage;

    /// <summary>Wallhaven random seed used for stable page walking, when known.</summary>
    public string? Seed
    {
        get => _seed;
        init
        {
            var trimmed = value?.Trim();
            _seed = string.IsNullOrEmpty(trimmed) ? null
                : trimmed.Length > MaximumSeedLength ? trimmed[..MaximumSeedLength] : trimmed;
        }
    }
    private string? _seed;

    /// <summary>How many images one fetch may take from this feed. Clamped 1..10.</summary>
    public int MaximumItemsPerFetch
    {
        get => _maximumItemsPerFetch;
        init => _maximumItemsPerFetch = ClampMaximumItemsPerFetch(value);
    }
    private int _maximumItemsPerFetch = DefaultMaximumItemsPerFetch;

    /// <summary>Clamps an items-per-fetch value into 1..10.</summary>
    public static int ClampMaximumItemsPerFetch(int value) =>
        Math.Clamp(value, MinimumMaximumItemsPerFetch, MaximumMaximumItemsPerFetch);

    public void Validate()
    {
        if (!IsSafeToken(Id, MaximumIdLength))
            throw new ArgumentException("Feed id must be 1-64 characters: ASCII letters, digits, '-' or '_'.", nameof(Id));

        var name = Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength)
            throw new ArgumentException("Feed name must be 1-80 characters.", nameof(Name));

        ArgumentNullException.ThrowIfNull(Filters);
        Filters.Validate();

        if (CadenceMinutes < MinimumCadence || CadenceMinutes > MaximumCadence)
            throw new ArgumentOutOfRangeException(nameof(CadenceMinutes), "Cadence must be between 1 and 10080 minutes.");

        if (ConsecutiveFailures < 0 || ConsecutiveFailures > MaximumFailureCount)
            throw new ArgumentOutOfRangeException(nameof(ConsecutiveFailures), "Failure count must be between 0 and 20.");

        if (LastAppliedId is not null && !IsSafeToken(LastAppliedId, MaximumAppliedIdLength, allowEmpty: true))
            throw new ArgumentException("Last applied id must be at most 40 safe characters.", nameof(LastAppliedId));

        if (CurrentPage < FirstPage)
            throw new ArgumentOutOfRangeException(nameof(CurrentPage), "Current page must be 1 or greater.");

        if (LastPage is { } lastPage && lastPage < FirstPage)
            throw new ArgumentOutOfRangeException(nameof(LastPage), "Last page must be 1 or greater.");

        if (MaximumItemsPerFetch < MinimumMaximumItemsPerFetch || MaximumItemsPerFetch > MaximumMaximumItemsPerFetch)
            throw new ArgumentOutOfRangeException(nameof(MaximumItemsPerFetch), "Items per fetch must be between 1 and 10.");
    }

    /// <summary>Shared by the model and the store so both agree on what a safe token is.</summary>
    internal static bool IsSafeToken(string? value, int maximumLength, bool allowEmpty = false)
    {
        if (value is null)
            return false;

        var token = value.Trim();
        if (token.Length == 0)
            return allowEmpty;
        if (token.Length > maximumLength)
            return false;

        foreach (var c in token)
        {
            var safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!safe)
                return false;
        }
        return true;
    }
}
