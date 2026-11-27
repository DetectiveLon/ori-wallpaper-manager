using System.Net;
using System.Net.Http;
using Ori.Updater;

namespace Ori.Desktop.Tests;

/// <summary>
/// A manifest URL that starts on HTTPS must not be usable to reach an HTTP
/// endpoint. The updater disables automatic redirects and checks every hop.
/// </summary>
public sealed class UpdaterRedirectTests
{
    [Fact]
    public async Task Redirect_to_http_is_refused_before_the_http_request_is_made()
    {
        var requested = new List<Uri>();
        using var handler = new RedirectHandler(request =>
        {
            requested.Add(request.RequestUri!);
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://attacker.example/update-manifest.json");
            return redirect;
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            SecureHttp.GetAsync(http, "https://github.com/DetectiveLon/ori-wallpaper-manager/update-manifest.json"));

        Assert.Contains("HTTPS", error.Message);
        var request = Assert.Single(requested);
        Assert.Equal("https", request.Scheme);
    }

    [Fact]
    public async Task Https_redirect_to_a_different_https_host_is_followed()
    {
        var requested = new List<Uri>();
        using var handler = new RedirectHandler(request =>
        {
            requested.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "first.example")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://second.example/update-manifest.json");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });
        using var http = new HttpClient(handler);

        using var response = await SecureHttp.GetAsync(http, "https://first.example/update-manifest.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requested.Count);
        Assert.All(requested, uri => Assert.Equal("https", uri.Scheme));
    }

    [Fact]
    public async Task Plain_http_url_is_refused_without_any_request()
    {
        var requested = new List<Uri>();
        using var handler = new RedirectHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<UpdateException>(() =>
            SecureHttp.GetAsync(http, "http://github.com/update-manifest.json"));

        Assert.Empty(requested);
    }

    private sealed class RedirectHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
