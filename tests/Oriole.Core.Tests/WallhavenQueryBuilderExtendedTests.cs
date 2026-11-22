using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

public sealed class WallhavenQueryBuilderExtendedTests
{
    [Fact]
    public void HotSortIsExposedAsWallhavenHot()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            Sorting = WallhavenSort.Hot
        });

        Assert.Contains("sorting=hot", uri.Query);
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
