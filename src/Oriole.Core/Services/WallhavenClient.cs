using System.Net;
using System.Reflection;
using System.Text.Json;
using Oriole.Core.Models;

namespace Oriole.Core.Services;

/// <summary>Small, bounded client for Wallhaven's documented public v1 API.</summary>
public sealed class WallhavenClient
{
    public const int MaximumJsonBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http;

    public static HttpClient CreateHttpClient()
    {
        // API-key headers must never be forwarded automatically to a redirect target.
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public WallhavenClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (HasAutoRedirectEnabled(http))
            throw new ArgumentException(
                "An auto-redirecting HttpClient can forward the X-API-Key header to another host. Use CreateHttpClient().",
                nameof(http));
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Oriole-Wallpaper/0.1 (+https://github.com/)");
    }

    private static bool HasAutoRedirectEnabled(HttpClient client)
    {
        // Best-effort invariant check: the redirect guard below only sees 3xx responses
        // when the handler does not follow them internally.
        object? handler = null;
        for (var clientType = typeof(HttpClient); clientType is not null && handler is null; clientType = clientType.BaseType)
        {
            var field = clientType.GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
            handler = field?.GetValue(client);
        }

        for (var type = handler?.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty("AllowAutoRedirect", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(handler) is bool enabled)
                return enabled;
        }
        return false;
    }

    public async Task<WallhavenSearchPage> SearchAsync(
        WallhavenFilters filters,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var uri = WallhavenQueryBuilder.Build(filters);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("An API key may only be sent over HTTPS.");
            request.Headers.Add("X-API-Key", apiKey.Trim());
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Wallhaven rejected the request. Check the API key and selected content filters.");

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("Wallhaven returned a redirect. The request was not followed to protect API credentials.");

        if ((int)response.StatusCode == 429)
        {
            var retry = response.Headers.RetryAfter?.Delta;
            var message = retry is null
                ? "Wallhaven rate limit reached. Wait before searching again."
                : $"Wallhaven rate limit reached. Retry in about {Math.Ceiling(retry.Value.TotalSeconds)} seconds.";
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumJsonBytes)
            throw new InvalidDataException("Wallhaven returned a response larger than the allowed 2 MiB.");

        var json = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var items = new List<WallhavenWallpaper>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!TryReadWallpaper(item, out var wallpaper))
                    continue;
                items.Add(wallpaper);
            }
        }

        var currentPage = ReadInt(root, "meta", "current_page") ?? filters.Page;
        var lastPage = ReadInt(root, "meta", "last_page") ?? currentPage;
        var seed = ReadString(root, "meta", "seed");
        return new WallhavenSearchPage(items, currentPage, lastPage, seed);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumJsonBytes)
                throw new InvalidDataException("Wallhaven returned a response larger than the allowed 2 MiB.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    private static bool TryReadWallpaper(JsonElement item, out WallhavenWallpaper wallpaper)
    {
        wallpaper = default!;
        var id = ReadString(item, "id");
        var page = ReadUri(item, "url");
        var image = ReadUri(item, "path");
        var thumbnail = ReadUri(item, "thumbs", "large") ?? ReadUri(item, "thumbs", "original");
        if (id is null || page is null || image is null || thumbnail is null)
            return false;

        var colors = new List<string>();
        if (item.TryGetProperty("colors", out var colorArray) && colorArray.ValueKind == JsonValueKind.Array)
        {
            colors.AddRange(colorArray.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(x => x.Length is 4 or 7));
        }

        wallpaper = new WallhavenWallpaper(
            id,
            page,
            image,
            thumbnail,
            ReadString(item, "category") ?? "unknown",
            ReadString(item, "purity") ?? "unknown",
            ReadInt(item, "dimension_x") ?? 0,
            ReadInt(item, "dimension_y") ?? 0,
            ReadString(item, "ratio") ?? string.Empty,
            ReadLong(item, "file_size") ?? 0,
            ReadString(item, "file_type") ?? string.Empty,
            colors);
        return true;
    }

    private static Uri? ReadUri(JsonElement element, params string[] path)
    {
        var value = ReadString(element, path);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }
        return element.TryGetInt32(out var value) ? value : null;
    }

    private static long? ReadLong(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }
        return element.TryGetInt64(out var value) ? value : null;
    }
}
