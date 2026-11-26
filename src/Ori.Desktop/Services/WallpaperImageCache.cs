using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Ori.Core.Models;

namespace Ori.Desktop.Services;

/// <summary>
/// Single source of truth for Wallhaven image fetching. The disk-backed path saves to
/// the local cache/library; an in-memory variant serves previews. Every URL and redirect
/// is restricted to Wallhaven's HTTPS image hosts and bytes/decoded dimensions are bounded.
/// </summary>
public sealed class WallpaperImageCache
{
    // Canonical, unified limits. The two image pipelines previously disagreed; these
    // are now shared by the disk cache and the in-memory preview/downloader path.
    // The 50 Mpx ceiling is the real decompression-bomb guard, so a 32k dimension cap
    // remains bounded while not rejecting wallpapers the UI already accepts.
    public const long MaximumFullImageBytes = 50L * 1024 * 1024;
    public const long MaximumThumbnailBytes = 5L * 1024 * 1024;
    public const long MaximumDecodedPixels = 50L * 1024 * 1024;
    public const int MaximumDimension = 32768;

    private const int MaximumRedirects = 4;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private readonly string _cacheDirectory;
    private readonly string _libraryImageDirectory;

    public WallpaperImageCache(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ori");
        _cacheDirectory = Path.Combine(root, "cache", "images");
        _libraryImageDirectory = Path.Combine(root, "library", "images");
    }

    /// <summary>Strict image-host check, applied to the initial URL and to each redirect.</summary>
    public static bool IsTrustedImageUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && (uri.IdnHost.Equals("w.wallhaven.cc", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("th.wallhaven.cc", StringComparison.OrdinalIgnoreCase));

    public Task<string> DownloadThumbnailAsync(
        WallhavenWallpaper wallpaper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        return DownloadToCacheAsync(wallpaper.ThumbnailUrl, wallpaper.Id, "thumb",
            MaximumThumbnailBytes, cancellationToken);
    }

    public Task<string> DownloadOriginalAsync(
        WallhavenWallpaper wallpaper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        return DownloadToCacheAsync(wallpaper.ImageUrl, wallpaper.Id, "full",
            MaximumFullImageBytes, cancellationToken);
    }

    /// <summary>
    /// In-memory variant used for previews. Shares the same validation and cache as the
    /// disk path; the caller receives only bytes and a media type.
    /// </summary>
    public async Task<WallhavenImageDownload> DownloadBytesAsync(
        Uri uri,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0 || maximumBytes > MaximumFullImageBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var kind = maximumBytes <= MaximumThumbnailBytes ? "thumb" : "full";
        var cachedPath = await DownloadToCacheAsync(
            uri, HashUri(uri), kind, maximumBytes, cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false);
        return new WallhavenImageDownload(bytes, MediaTypeFor(Path.GetExtension(cachedPath)));
    }

    public async Task<string> SaveToLibraryAsync(
        WallhavenWallpaper wallpaper,
        CancellationToken cancellationToken = default)
    {
        var cachedPath = await DownloadOriginalAsync(wallpaper, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_libraryImageDirectory);

        var extension = Path.GetExtension(cachedPath);
        var destination = Path.Combine(_libraryImageDirectory, $"wallhaven-{SafeKey(wallpaper.Id)}{extension}");
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var source = new FileStream(
                    cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var target = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(target, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        return destination;
    }

    private async Task<string> DownloadToCacheAsync(
        Uri uri,
        string id,
        string kind,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedImageUri(uri))
            throw new InvalidDataException("Only HTTPS images hosted on Wallhaven image hosts are allowed.");

        var prefix = $"wallhaven-{SafeKey(id)}-{kind}";
        Directory.CreateDirectory(_cacheDirectory);

        var existing = Directory.EnumerateFiles(_cacheDirectory, prefix + ".*")
            .FirstOrDefault(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && new FileInfo(path).Length > 0);
        if (existing is not null)
            return existing;

        var current = uri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                    throw new InvalidOperationException("The image server returned an invalid or excessive redirect chain.");

                var redirected = new Uri(current, response.Headers.Location);
                if (!IsTrustedImageUri(redirected))
                    throw new InvalidOperationException("The image server attempted to redirect outside Wallhaven.");
                current = redirected;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new FileNotFoundException("The wallpaper image is no longer available.");
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
                throw new InvalidDataException("The image exceeds the allowed download size.");

            var temporaryPath = Path.Combine(
                _cacheDirectory, prefix + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[64 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        total += read;
                        if (total > maximumBytes)
                            throw new InvalidDataException("The image exceeds the allowed download size.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    if (total == 0)
                        throw new InvalidDataException("The image server returned an empty file.");
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var extension = ValidateImageFile(temporaryPath);
                var finalPath = Path.Combine(_cacheDirectory, prefix + extension);
                File.Move(temporaryPath, finalPath, overwrite: true);
                return finalPath;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    private static string SafeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 40
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The wallpaper ID contains unsupported characters.", nameof(value));
        }

        return value;
    }

    private static string HashUri(Uri uri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string MediaTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static string ValidateImageFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[16];
        var count = stream.Read(header);

        string? extension = null;
        if (count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            extension = ".jpg";
        else if (count >= PngSignature.Length && header[..PngSignature.Length].SequenceEqual(PngSignature))
            extension = ".png";
        else if (count >= 3 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
            extension = ".gif";
        else if (count >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
            extension = ".bmp";
        else if (count >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            extension = ".webp";

        if (extension is null)
            throw new InvalidDataException("The downloaded file is not a supported image.");

        stream.Position = 0;
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("The downloaded image contains no frames.");

        var frame = decoder.Frames[0];
        var pixels = (long)frame.PixelWidth * frame.PixelHeight;
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            || frame.PixelWidth > MaximumDimension
            || frame.PixelHeight > MaximumDimension
            || pixels > MaximumDecodedPixels)
        {
            throw new InvalidDataException("The image dimensions exceed Ori's safety limits.");
        }

        return extension;
    }
}
