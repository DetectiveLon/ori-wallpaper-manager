using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

public sealed class WallhavenQueryBuilderRegressionTests
{
    [Fact]
    public void SeedIsIncludedAndQueryIsAlwaysHttps()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters { Seed = "s3ed" });

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("wallhaven.cc", uri.Host);
        Assert.Contains("seed=s3ed", uri.Query);
    }

    [Fact]
    public void ExactResolutionSuppressesMinimumResolution()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters { ExactResolution = "2560x1440" });

        Assert.Contains("resolutions=2560x1440", uri.Query);
        Assert.DoesNotContain("atleast=", uri.Query);
    }

    [Fact]
    public void ColorsAreDeduplicatedAndStrippedOfHash()
    {
        var uri = WallhavenQueryBuilder.Build(new WallhavenFilters
        {
            Colors = ["#660000", "660000", "ffffff"]
        });

        Assert.Contains("colors=660000%2Cffffff", uri.Query, StringComparison.OrdinalIgnoreCase);
    }
}
