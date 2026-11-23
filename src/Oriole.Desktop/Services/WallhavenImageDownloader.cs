using System.Net;
using System.Net.Http;

namespace Oriole.Desktop.Services;

public sealed record WallhavenImageDownload(byte[] Bytes, string MediaType);

/// <summary>Bounded image downloads restricted to Wallhaven's HTTPS host family.</summary>
public sealed class WallhavenImageDownloader
{
    public const int MaximumThumbnailBytes = 5 * 1024 * 1024;
    public const int MaximumWallpaperBytes = 50 * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = TimeSpan.FromSeconds(45) };

    public static bool IsTrustedUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && (uri.IdnHost.Equals("wallhaven.cc", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".wallhaven.cc", StringComparison.OrdinalIgnoreCase));

    public async Task<WallhavenImageDownload> DownloadAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedUri(uri))
            throw new InvalidOperationException("The image URL is not a trusted Wallhaven HTTPS address.");
        if (maximumBytes is <= 0 or > MaximumWallpaperBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var current = uri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirectCount >= 4 || response.Headers.Location is null)
                    throw new InvalidOperationException("The image server returned an invalid or excessive redirect chain.");

                var redirected = new Uri(current, response.Headers.Location);
                if (!IsTrustedUri(redirected))
                    throw new InvalidOperationException("The image server attempted to redirect outside Wallhaven.");
                current = redirected;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
                throw new InvalidDataException($"The image exceeds the {maximumBytes:N0}-byte download limit.");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The server returned '{mediaType}', not an image.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                    throw new InvalidDataException($"The image exceeds the {maximumBytes:N0}-byte download limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (output.Length == 0)
                throw new InvalidDataException("The image server returned an empty file.");
            return new WallhavenImageDownload(output.ToArray(), mediaType);
        }
    }
}
