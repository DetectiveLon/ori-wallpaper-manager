using Ori.Updater;

namespace Ori.Desktop.Tests;

/// <summary>
/// scripts/publish.ps1 serializes the manifest with lowercase keys. The updater
/// must accept that exact JSON shape; a case-sensitive deserializer silently
/// rejected every manifest and made every update look like a failure.
/// </summary>
public sealed class UpdaterManifestParserTests
{
    private const string PublishedJson = """
        {
          "version": "1.0.0",
          "assetName": "ori-1.0.0-win-x64.exe",
          "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "url": "https://github.com/DetectiveLon/ori-wallpaper-manager/releases/download/v1.0.0/ori-1.0.0-win-x64.exe",
          "publishedAt": "2026-11-27T19:00:00.0000000Z"
        }
        """;

    [Fact]
    public void Parser_reads_the_exact_json_shape_written_by_publish_ps1()
    {
        var manifest = UpdateManifestParser.Parse(PublishedJson);

        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("ori-1.0.0-win-x64.exe", manifest.AssetName);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", manifest.Sha256);
        Assert.Equal(
            "https://github.com/DetectiveLon/ori-wallpaper-manager/releases/download/v1.0.0/ori-1.0.0-win-x64.exe",
            manifest.Url);
    }

    [Fact]
    public void Parser_accepts_pascal_case_keys_too()
    {
        var json = """
            {
              "Version": "2.3.4",
              "AssetName": "ori-2.3.4-win-x64.exe",
              "Sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "Url": "https://example.com/ori-2.3.4-win-x64.exe"
            }
            """;

        var manifest = UpdateManifestParser.Parse(json);

        Assert.Equal("2.3.4", manifest.Version);
        Assert.Equal("ori-2.3.4-win-x64.exe", manifest.AssetName);
    }

    [Fact]
    public void Parser_refuses_an_http_asset_url()
    {
        var json = PublishedJson.Replace(
            "https://github.com/", "http://github.com/", StringComparison.Ordinal);

        var error = Assert.Throws<UpdateException>(() => UpdateManifestParser.Parse(json));
        Assert.Contains("HTTPS", error.Message);
    }

    [Fact]
    public void Parser_refuses_an_asset_that_is_not_an_exe()
    {
        var json = PublishedJson.Replace("ori-1.0.0-win-x64.exe", "ori.zip");

        Assert.Throws<UpdateException>(() => UpdateManifestParser.Parse(json));
    }
}
