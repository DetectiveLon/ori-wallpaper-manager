using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Core.Tests;

public sealed class WallhavenQueryBuilderTests
{
    [Fact]
    public void DefaultFilters_RequestAllCategoriesAndSafeContent()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters());
        var query = Parse(uri);

        Assert.Equal("111", query["categories"]);
        Assert.Equal("100", query["purity"]);
        Assert.Equal("random", query["sorting"]);
    }

    [Fact]
    public void TagsPreserveRequiredAndExcludedOperators()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters { Query = "+landscape -people" });
        var query = Parse(uri);
        Assert.Equal("+landscape -people", query["q"]);
    }

    [Fact]
    public void TopRangeIsOnlySentForToplistSorting()
    {
        var regular = Parse(WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            Sorting = WallhavenSort.Random,
            TopRange = WallhavenTopRange.Year
        }));
        var top = Parse(WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            Sorting = WallhavenSort.Toplist,
            TopRange = WallhavenTopRange.Year
        }));

        Assert.False(regular.ContainsKey("topRange"));
        Assert.Equal("1y", top["topRange"]);
    }

    [Fact]
    public void MinimumResolutionAndRatiosAreIncluded()
    {
        var query = Parse(WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            MinimumResolution = "1920x1080",
            Ratio = "16x9",
            Colors = ["#000000", "ffffff"]
        }));

        Assert.Equal("1920x1080", query["atleast"]);
        Assert.Equal("16x9", query["ratios"]);
        Assert.Equal("000000,ffffff", query["colors"]);
    }

    [Fact]
    public void ExactResolutionIsIncluded()
    {
        var query = Parse(WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            ExactResolution = "3840x2160"
        }));

        Assert.Equal("3840x2160", query["resolutions"]);
        Assert.False(query.ContainsKey("atleast"));
    }

    [Fact]
    public void MinimumAndExactResolutionCannotBeCombined()
    {
        Assert.Throws<ArgumentException>(() => WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            MinimumResolution = "1920x1080",
            ExactResolution = "3840x2160"
        }));
    }

    [Fact]
    public void InvalidEmptyCategorySelectionIsRejected()
    {
        Assert.Throws<ArgumentException>(() => WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            Categories = WallhavenCategory.None
        }));
    }

    private static Dictionary<string, string> Parse(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
}
