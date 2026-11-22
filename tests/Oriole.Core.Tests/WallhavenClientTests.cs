using System.Net;
using System.Net.Http;
using Oriole.Core.Models;
using Oriole.Core.Services;

namespace Oriole.Core.Tests;

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
