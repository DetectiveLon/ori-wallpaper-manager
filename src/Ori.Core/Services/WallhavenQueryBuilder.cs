using Ori.Core.Models;

namespace Ori.Core.Services;

public static class WallhavenQueryBuilder
{
    public static Uri Build(WallhavenFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        filters.Validate();

        var values = new List<KeyValuePair<string, string>>
        {
            Pair("categories", EncodeBits(filters.Categories, WallhavenCategory.General, WallhavenCategory.Anime, WallhavenCategory.People)),
            Pair("purity", EncodeBits(filters.Purity, WallhavenPurity.Safe, WallhavenPurity.Sketchy, WallhavenPurity.Explicit)),
            Pair("sorting", filters.Sorting switch
            {
                WallhavenSort.DateAdded => "date_added",
                WallhavenSort.Relevance => "relevance",
                WallhavenSort.Random => "random",
                WallhavenSort.Views => "views",
                WallhavenSort.Favorites => "favorites",
                WallhavenSort.Toplist => "toplist",
                _ => throw new ArgumentOutOfRangeException(nameof(filters.Sorting))
            }),
            Pair("order", filters.Order switch
            {
                WallhavenOrder.Descending => "desc",
                WallhavenOrder.Ascending => "asc",
                _ => throw new ArgumentOutOfRangeException(nameof(filters.Order))
            }),
            Pair("page", filters.Page.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrWhiteSpace(filters.Query))
            values.Add(Pair("q", filters.Query.Trim()));

        if (filters.Sorting == WallhavenSort.Toplist)
            values.Add(Pair("topRange", filters.TopRange switch
            {
                WallhavenTopRange.Day => "1d",
                WallhavenTopRange.ThreeDays => "3d",
                WallhavenTopRange.Week => "1w",
                WallhavenTopRange.Month => "1M",
                WallhavenTopRange.ThreeMonths => "3M",
                WallhavenTopRange.SixMonths => "6M",
                WallhavenTopRange.Year => "1y",
                _ => throw new ArgumentOutOfRangeException(nameof(filters.TopRange))
            }));

        if (!string.IsNullOrWhiteSpace(filters.MinimumResolution))
            values.Add(Pair("atleast", filters.MinimumResolution));

        if (!string.IsNullOrWhiteSpace(filters.ExactResolution))
            values.Add(Pair("resolutions", filters.ExactResolution));

        if (!string.IsNullOrWhiteSpace(filters.Ratio))
            values.Add(Pair("ratios", filters.Ratio));

        if (filters.Colors.Count > 0)
            values.Add(Pair("colors", string.Join(',', filters.Colors.Select(c => c.Trim().TrimStart('#')).Distinct(StringComparer.OrdinalIgnoreCase))));

        if (!string.IsNullOrWhiteSpace(filters.Seed))
            values.Add(Pair("seed", filters.Seed));

        var query = string.Join('&', values.Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}"));
        return new Uri($"https://wallhaven.cc/api/v1/search?{query}", UriKind.Absolute);
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    private static string EncodeBits<TEnum>(TEnum selected, TEnum first, TEnum second, TEnum third)
        where TEnum : struct, Enum
    {
        var bits = Convert.ToInt64(selected);
        return string.Concat(
            (bits & Convert.ToInt64(first)) != 0 ? '1' : '0',
            (bits & Convert.ToInt64(second)) != 0 ? '1' : '0',
            (bits & Convert.ToInt64(third)) != 0 ? '1' : '0');
    }
}
