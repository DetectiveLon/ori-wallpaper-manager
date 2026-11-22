using System.Net;
using System.Windows.Media.Imaging;
using Oriole.Core.Models;

namespace Oriole.Desktop.Services;

/// <summary>
/// Bounded, disk-backed Wallhaven image cache. Only HTTPS image URLs from Wallhaven's
/// image host are accepted. Downloads are streamed to a temporary file and promoted
/// only after size, signature, and decoded-dimension checks succeed.
/// </summary>
public sealed class WallpaperImageCache
{
    public const long MaximumFullImageBytes = 50L * 1024 * 1024;
    public const long MaximumThumbnailBytes = 8L * 1024 * 1024;
    private const long MaximumDecodedPixels = 64L * 1024 * 1024;
    private const int MaximumDimension = 12000;

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
            "Oriole");
        _cacheDirectory = Path.Combine(root, "cache", "images");
        _libraryImageDirectory = Path.Combine(root, "library", "images");
    }

    public Task<string> DownloadThumbnailAsync(
        WallhavenWallpaper wallpaper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        return DownloadAsync(wallpaper.ThumbnailUrl, wallpaper.Id, "thumb",
            MaximumThumbnailBytes, cancellationToken);
    }

    public Task<string> DownloadOriginalAsync(
        WallhavenWallpaper wallpaper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        return DownloadAsync(wallpaper.ImageUrl, wallpaper.Id, "full",
            MaximumFullImageBytes, cancellationToken);
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

    private async Task<string> DownloadAsync(
        Uri uri,
        string id,
        string kind,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateImageUri(uri);
        var prefix = $"wallhaven-{SafeKey(id)}-{kind}";
        Directory.CreateDirectory(_cacheDirectory);

        var existing = Directory.EnumerateFiles(_cacheDirectory, prefix + ".*")
            .FirstOrDefault(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        if (existing is not null && new FileInfo(existing).Length > 0)
            return existing;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("The image server redirected the request. The redirect was not followed.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("The wallpaper image is no longer available.");
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("The image exceeds the allowed download size.");

        var temporaryPath = Path.Combine(_cacheDirectory, prefix + "." + Guid.NewGuid().ToString("N") + ".tmp");
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

    private static void ValidateImageUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || (!string.Equals(uri.Host, "w.wallhaven.cc", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "th.wallhaven.cc", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Only HTTPS images hosted on Wallhaven image hosts are allowed.");
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

    private static string ValidateImageFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        var count = stream.Read(header);
        var extension = count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF
            ? ".jpg"
            : count >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                ? ".png"
                : null;

        if (extension is null)
            throw new InvalidDataException("The downloaded file is not a supported JPEG or PNG image.");

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
            throw new InvalidDataException("The image dimensions exceed Oriole's safety limits.");
        }

        return extension;
    }
}
