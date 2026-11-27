using System.Net.Http;

namespace Ori.Desktop.Services;

public sealed record WallhavenImageDownload(byte[] Bytes, string MediaType);

/// <summary>
/// Backwards-compatible facade for in-memory image fetches. All host validation,
/// redirect handling, size limits and dimension checks now live in
/// <see cref="WallpaperImageCache"/>, which is the single source of truth.
/// </summary>
public sealed class WallhavenImageDownloader
{
    // Kept as int constants so existing callers compile unchanged; the values come
    // from the canonical limits in WallpaperImageCache.
    public const int MaximumThumbnailBytes = (int)WallpaperImageCache.MaximumThumbnailBytes;
    public const int MaximumWallpaperBytes = (int)WallpaperImageCache.MaximumFullImageBytes;
    public const string UnsupportedFormatMessage = WallpaperImageCache.UnsupportedFormatMessage;

    /// <summary>True only for formats Windows can apply as a wallpaper.</summary>
    public static bool IsSupportedImageExtension(string extension) =>
        WallpaperImageCache.IsSupportedImageExtension(extension);

    private static readonly WallpaperImageCache Cache = new();

    /// <summary>
    /// General Wallhaven host check (the apex domain and any subdomain). Used for page
    /// links as well as images; the stricter image-only check is in the cache.
    /// </summary>
    public static bool IsTrustedUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && (uri.IdnHost.Equals("wallhaven.cc", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".wallhaven.cc", StringComparison.OrdinalIgnoreCase));

    public Task<WallhavenImageDownload> DownloadAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedUri(uri))
            throw new InvalidOperationException("The image URL is not a trusted Wallhaven HTTPS address.");
        if (maximumBytes is <= 0 or > MaximumWallpaperBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        return Cache.DownloadBytesAsync(uri, maximumBytes, cancellationToken);
    }
}
