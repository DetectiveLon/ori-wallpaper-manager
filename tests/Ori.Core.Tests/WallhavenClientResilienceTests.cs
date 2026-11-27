using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Ori.Core.Models;
using Ori.Core.Services;

namespace Ori.Core.Tests;

public sealed class WallhavenClientResilienceTests
{
    [Fact]
    public async Task SearchRetries429AndHonoursSaneRetryAfter()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new CountingHandler(request =>
        {
            calls++;
            return calls <= 2
                ? RateLimited(TimeSpan.FromMilliseconds(100))
                : JsonResponse("""{"data":[],"meta":{"current_page":1,"last_page":1}}""");
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var result = await client.SearchAsync(new WallhavenFilters());

        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(100), delay));
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchWithoutRetryAfterUsesBoundedExponentialBackoff()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new CountingHandler(_ =>
        {
            calls++;
            return calls <= 2
                ? RateLimited()
                : JsonResponse("""{"data":[],"meta":{"current_page":1,"last_page":1}}""");
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await client.SearchAsync(new WallhavenFilters());

        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay =>
        {
            Assert.InRange(delay.TotalMilliseconds, 50, 8_000);
        });
    }

    [Fact]
    public async Task SearchStopsAfterMaximumRetriesAndThrows429()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new CountingHandler(_ =>
        {
            calls++;
            return RateLimited(TimeSpan.FromMilliseconds(10));
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchAsync(new WallhavenFilters()));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1 + WallhavenClient.MaximumRateLimitRetries, calls);
        Assert.Equal(WallhavenClient.MaximumRateLimitRetries, delays.Count);
    }

    [Fact]
    public async Task SearchHonoursCancellationDuringBackoff()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CountingHandler(_ => RateLimited(TimeSpan.FromSeconds(2)));
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http, (delay, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SearchAsync(new WallhavenFilters(), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task FetchTagsReadsNamesFromDetailEndpoint()
    {
        Uri? requested = null;
        var handler = new CountingHandler(request =>
        {
            requested = request.RequestUri;
            return JsonResponse("""
            {
              "data": {
                "id": "abc123",
                "tags": [
                  { "name": "landscape" },
                  { "name": "nature" },
                  { "name": "   " }
                ]
              }
            }
            """);
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        var tags = await client.FetchTagsAsync("abc123");

        Assert.Equal(new[] { "landscape", "nature" }, tags);
        Assert.Equal("/api/v1/w/abc123", requested?.AbsolutePath);
    }

    [Fact]
    public async Task FetchTagsReturnsEmptyForServerError()
    {
        var calls = 0;
        var handler = new CountingHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("failure")
            };
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        Assert.Empty(await client.FetchTagsAsync("abc123"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FetchTagsReturnsEmptyForRedirectWithoutFollowingIt()
    {
        var calls = 0;
        var handler = new CountingHandler(_ =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://wallhaven.cc/w/other");
            return redirect;
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        Assert.Empty(await client.FetchTagsAsync("abc123"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FetchTagsReturnsEmptyForUnsafeIdWithoutAnyRequest()
    {
        var calls = 0;
        var handler = new CountingHandler(_ =>
        {
            calls++;
            return JsonResponse("""{"data":{"tags":[]}}""");
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        Assert.Empty(await client.FetchTagsAsync("../admin"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RedirectToAnotherHostNeverReceivesTheApiKey()
    {
        var observed = new List<(string Host, string? Key)>();
        var handler = new CountingHandler(request =>
        {
            var key = request.Headers.TryGetValues("X-API-Key", out var values)
                ? values.SingleOrDefault()
                : null;
            observed.Add((request.RequestUri!.Host, key));
            if (request.RequestUri.Host == "wallhaven.cc")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://third-party.example/steal");
                return redirect;
            }
            return JsonResponse("""{"data":[],"meta":{"current_page":1,"last_page":1}}""");
        });
        using var http = new HttpClient(handler);
        var client = new WallhavenClient(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SearchAsync(new WallhavenFilters(), "secret-key"));

        // One request only: the 302 was not followed, so the key never left the API host.
        var request = Assert.Single(observed);
        Assert.Equal("wallhaven.cc", request.Host);
        Assert.Equal("secret-key", request.Key);
    }

    private static HttpResponseMessage RateLimited(TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (retryAfter is { } value)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(value);
        return response;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
