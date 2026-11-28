using System.Security.Cryptography;
using System.Text.Json;

namespace Ori.Desktop.Tests;

/// <summary>
/// publish.ps1 writes update-manifest.json for Ori.Updater. The manifest version,
/// asset name, download URL and hash must describe the same EXE; a mismatch here
/// makes every client reject the update or, worse, download the wrong file.
/// When a generated manifest is present under artifacts/, the test parses that
/// real file; otherwise it parses an equivalent fixture so the contract is still
/// checked on a clean checkout.
/// </summary>
public sealed class UpdateManifestTests
{
    [Fact]
    public void Generated_manifest_version_and_asset_are_aligned()
    {
        var generated = FindGeneratedManifest();
        var json = generated is null
            ? SampleManifest()
            : File.ReadAllText(generated);

        AssertManifest(json, generated);
    }

    private static string? FindGeneratedManifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "artifacts", "update-manifest.json");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string SampleManifest()
    {
        var sha = Convert.ToHexString(new byte[32]).ToLowerInvariant();
        return $$"""
        {
          "version": "1.0.0",
          "assetName": "ori-1.0.0-win-x64.exe",
          "sha256": "{{sha}}",
          "url": "https://github.com/DetectiveLon/ori-wallpaper-manager/releases/download/v1.0.0/ori-1.0.0-win-x64.exe",
          "publishedAt": "2026-11-27T19:00:00.0000000Z"
        }
        """;
    }

    private static void AssertManifest(string json, string? generatedPath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("version", out var versionElement));
        var version = versionElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Matches(@"^\d+(\.\d+){1,3}$", version!);

        Assert.True(root.TryGetProperty("assetName", out var assetElement));
        var assetName = assetElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(assetName));
        Assert.Equal($"ori-{version}-win-x64.exe", assetName);

        Assert.True(root.TryGetProperty("url", out var urlElement));
        var url = urlElement.GetString();
        Assert.StartsWith("https://github.com/", url);
        Assert.EndsWith($"/releases/download/v{version}/{assetName}", url);

        Assert.True(root.TryGetProperty("sha256", out var hashElement));
        var sha256 = hashElement.GetString();
        Assert.Matches("^[0-9a-f]{64}$", sha256);

        Assert.True(root.TryGetProperty("publishedAt", out var publishedElement));
        Assert.True(DateTimeOffset.TryParse(publishedElement.GetString(), out _));

        // If the actual release asset is sitting next to the generated manifest,
        // the recorded hash must describe that exact file.
        if (generatedPath is not null)
        {
            var assetPath = Path.Combine(Path.GetDirectoryName(generatedPath)!, assetName!);
            if (File.Exists(assetPath))
            {
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assetPath)))
                    .ToLowerInvariant();
                Assert.Equal(actual, sha256);
            }
        }
    }
}
