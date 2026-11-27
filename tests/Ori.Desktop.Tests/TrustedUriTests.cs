using Ori.Desktop.Services;

namespace Ori.Desktop.Tests;

/// <summary>
/// Host allowlists are a security boundary: a look-alike host or a userinfo trick
/// must never be treated as a Wallhaven image/page URL. These are pure helpers;
/// no HTTP request is made.
/// </summary>
public sealed class TrustedUriTests
{
    [Theory]
    [InlineData("https://w.wallhaven.cc/full/8g/wallhaven-8g1234.jpg", true)]
    [InlineData("https://th.wallhaven.cc/small/8g/8g1234.jpg", true)]
    [InlineData("HTTPS://W.WALLHAVEN.CC/full/8g/wallhaven-8g1234.jpg", true)]
    [InlineData("https://w.wallhaven.cc:8443/full/8g/x.jpg", true)]
    [InlineData("http://w.wallhaven.cc/full/8g/x.jpg", false)]
    [InlineData("https://wallhaven.cc/w/8g1234", false)]
    [InlineData("https://th.wallhaven.cc.evil.example/full/x.jpg", false)]
    [InlineData("https://w.wallhaven.cc@evil.example/full/x.jpg", false)]
    [InlineData("https://evil.example/?next=https://w.wallhaven.cc/full/x.jpg", false)]
    [InlineData("ftp://w.wallhaven.cc/full/x.jpg", false)]
    public void IsTrustedImageUri_accepts_only_https_wallhaven_image_hosts(string value, bool expected)
    {
        Assert.Equal(expected, WallpaperImageCache.IsTrustedImageUri(new Uri(value, UriKind.Absolute)));
    }

    [Fact]
    public void IsTrustedImageUri_rejects_null_and_relative_uris()
    {
        Assert.False(WallpaperImageCache.IsTrustedImageUri(null));
        Assert.False(WallpaperImageCache.IsTrustedImageUri(new Uri("/full/8g/x.jpg", UriKind.Relative)));
    }

    [Theory]
    [InlineData("https://wallhaven.cc/w/8g1234", true)]
    [InlineData("https://wallhaven.cc/", true)]
    [InlineData("https://w.wallhaven.cc/full/8g/x.jpg", true)]
    [InlineData("https://th.wallhaven.cc/small/8g/x.jpg", true)]
    [InlineData("http://wallhaven.cc/w/8g1234", false)]
    [InlineData("https://wallhaven.cc.evil.example/w/8g1234", false)]
    [InlineData("https://wallhaven.cc@evil.example/w/8g1234", false)]
    [InlineData("https://wwallhaven.cc/w/8g1234", false)]
    [InlineData("https://notwallhaven.cc/w/8g1234", false)]
    [InlineData("ftp://wallhaven.cc/w/8g1234", false)]
    public void IsTrustedUri_accepts_https_apex_and_real_subdomains_only(string value, bool expected)
    {
        Assert.Equal(expected, WallhavenImageDownloader.IsTrustedUri(new Uri(value, UriKind.Absolute)));
    }

    [Fact]
    public void IsTrustedUri_rejects_null_and_relative_uris()
    {
        Assert.False(WallhavenImageDownloader.IsTrustedUri(null));
        Assert.False(WallhavenImageDownloader.IsTrustedUri(new Uri("/w/8g1234", UriKind.Relative)));
    }
}
