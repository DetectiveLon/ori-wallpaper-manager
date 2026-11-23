using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

public sealed class WallhavenQueryBuilderExtendedTests
{
    [Theory]
    [InlineData(WallhavenSort.DateAdded, "date_added")]
    [InlineData(WallhavenSort.Relevance, "relevance")]
    [InlineData(WallhavenSort.Random, "random")]
    [InlineData(WallhavenSort.Views, "views")]
    [InlineData(WallhavenSort.Favorites, "favorites")]
    [InlineData(WallhavenSort.Toplist, "toplist")]
    public void SupportedSortsAreSerializedToDocumentedWallhavenValues(
        WallhavenSort sorting,
        string expectedValue)
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters { Sorting = sorting });

        Assert.Contains($"sorting={expectedValue}", uri.Query);
    }

    [Fact]
    public void DefaultCategoriesAndPurityRemainSafe()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters());

        Assert.Contains("categories=111", uri.Query);
        Assert.Contains("purity=100", uri.Query);
    }

    [Fact]
    public void SearchExpressionIsBounded()
    {
        var filters = new WallhavenFilters { Query = new string('a', 301) };

        Assert.Throws<ArgumentException>(() => WallhavenQueryBuilder.Build(filters));
    }

    [Fact]
    public void ColorPaletteCanContainMultipleSelectedColors()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            Colors = new[] { "660000", "ffffff" }
        });

        Assert.Contains("colors=660000%2Cffffff", uri.Query, StringComparison.OrdinalIgnoreCase);
    }
}
