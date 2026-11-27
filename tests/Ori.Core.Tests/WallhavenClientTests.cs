using System.Net;
using System.Net.Http;
using System.Reflection;
using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Core.Tests;

public sealed class WallhavenClientTests
{
    [Fact]
    public async Task Search_UsesApiKeyHeaderAndReadsWallpaperMetadata()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("test-key", request.Headers.GetValues("X-API-Key").Single());
            return JsonResponse("""
            {
              "data": [
                {
                  "id": "abc123",
                  "url": "https://wallhaven.cc/w/abc123",
                  "path": "https://w.wallhaven.cc/full/ab/wallhaven-abc123.jpg",
                  "category": "anime",
                  "purity": "sfw",
                  "dimension_x": 3840,
                  "dimension_y": 2160,
                  "ratio": "1.78",
                  "file_size": 1234567,
                  "file_type": "image/jpeg",
                  "colors": ["#000000", "#ffffff"],
                  "thumbs": {
                    "large": "https://th.wallhaven.cc/lg/ab/abc123.jpg"
                  }
                }
              ],
              "meta": { "current_page": 1, "last_page": 4, "seed": "a1b2c3" }
            }
            """);
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        var result = await client.SearchAsync(new WallhavenFilters(), "test-key");

        var wallpaper = Assert.Single(result.Items);
        Assert.Equal("abc123", wallpaper.Id);
        Assert.Equal(3840, wallpaper.Width);
        Assert.Equal(2160, wallpaper.Height);
        Assert.Equal(4, result.LastPage);
        Assert.Equal("a1b2c3", result.Seed);
    }

    [Fact]
    public async Task OversizedJsonResponseIsRejectedBeforeParsing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[WallhavenClient.MaximumJsonBytes + 1])
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(new WallhavenFilters()));
    }

    [Fact]
    public void CreateHttpClientDisablesRedirectsAndUsesThirtySecondTimeout()
    {
        using var http = WallhavenClient.CreateHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
        Assert.False(ReadAllowAutoRedirect(http));
    }

    [Fact]
    public async Task SearchSendsTheSharedVersionedUserAgent()
    {
        string? userAgent = null;
        var handler = new StubHandler(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return JsonResponse("""{"data":[],"meta":{"current_page":1,"last_page":1}}""");
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        await client.SearchAsync(new WallhavenFilters());

        Assert.Equal(Ori.Core.OriVersion.UserAgent, userAgent);
        Assert.StartsWith("Ori-Wallpaper/", userAgent!);
    }

    [Fact]
    public async Task ChunkedOversizedResponseIsRejectedWithoutContentLength()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedContent(new byte[WallhavenClient.MaximumJsonBytes + 1])
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(new WallhavenFilters()));
    }

    private static bool ReadAllowAutoRedirect(HttpClient client)
    {
        object? handler = null;
        for (var type = typeof(HttpClient); type is not null && handler is null; type = type.BaseType)
            handler = type.GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client);

        for (var type = handler?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetProperty("AllowAutoRedirect", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(handler) is bool enabled)
            {
                return enabled;
            }
        }
        throw new InvalidOperationException("Could not inspect the HttpClient handler.");
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false; // no Content-Length: forces the streaming size guard
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
