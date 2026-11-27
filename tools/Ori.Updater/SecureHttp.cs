using System.Net.Http;

namespace Ori.Updater;

/// <summary>
/// HTTPS-only fetching. The configured URL is validated, automatic redirects
/// are disabled on the real client, and every redirect hop is resolved and
/// scheme-checked manually so a 302 cannot downgrade the download to http://.
/// The final response URI is checked again before the caller parses or hashes
/// the content.
/// </summary>
public static class SecureHttp
{
    private const int MaxRedirects = 5;

    public static Uri RequireHttps(string url, string message = "The update URL must be HTTPS.")
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(message);
        }
        return uri;
    }

    public static Uri RequireHttps(Uri uri, string message = "The update URL must be HTTPS.")
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(message);
        return uri;
    }

    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string url,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var uri = RequireHttps(url);

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, completion, cancellationToken)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    response.Dispose();
                    throw new UpdateException("Redirect response has no Location header.");
                }

                var next = new Uri(uri, location);
                response.Dispose();
                RequireHttps(next, "The update URL was redirected to a non-HTTPS address.");
                if (hop >= MaxRedirects)
                    throw new UpdateException("Too many redirects while fetching an update.");
                uri = next;
                continue;
            }

            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                throw new UpdateException("The final update URL is not HTTPS.");
            }

            return response;
        }
    }
}
