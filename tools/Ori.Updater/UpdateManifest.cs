using System.Text.Json;

namespace Ori.Updater;

/// <summary>
/// The exact shape written by scripts/publish.ps1. Extra fields such as
/// publishedAt are deliberately ignored so old/new manifests stay compatible.
/// </summary>
public sealed record UpdateManifest(string Version, string AssetName, string Sha256, string Url);

/// <summary>
/// Parses and validates the update manifest. publish.ps1 writes camelCase keys,
/// so web defaults (camelCase + case-insensitive) are required; the previous
/// case-sensitive deserializer rejected every published manifest.
/// </summary>
public static class UpdateManifestParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static UpdateManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Validate(JsonSerializer.Deserialize<UpdateManifest>(json, Options));
    }

    public static async Task<UpdateManifest> ParseAsync(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Validate(await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, Options)
            .ConfigureAwait(false));
    }

    private static UpdateManifest Validate(UpdateManifest? manifest)
    {
        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.AssetName) ||
            string.IsNullOrWhiteSpace(manifest.Sha256) ||
            string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new UpdateException("The update manifest is missing required fields.");
        }

        SecureHttp.RequireHttps(manifest.Url, "The update asset URL must be HTTPS.");

        var assetName = Path.GetFileName(manifest.AssetName);
        if (string.IsNullOrWhiteSpace(assetName) ||
            !assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The update asset must be a .exe file name.");
        }

        return manifest with { AssetName = assetName };
    }
}
