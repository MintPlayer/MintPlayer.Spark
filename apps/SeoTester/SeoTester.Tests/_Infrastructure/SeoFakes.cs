using System.Collections.Concurrent;
using System.Net;
using SeoTester.Verification;

namespace SeoTester.Tests._Infrastructure;

/// <summary>DNS answered from a table (the resolver abstraction, F3): nothing leaves the machine.</summary>
public sealed class FakeDns : IDnsTxtResolver
{
    public ConcurrentDictionary<string, string[]> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(Txt.TryGetValue(name, out var records) ? records : []);
}

/// <summary>
/// The web as seen by the verification client: absolute URL → (status, body, location). Anything else is a 404. It
/// replaces only the client's primary handler, so the SSRF-safe handler's redirect rules still run around it.
/// </summary>
public sealed class FakeWeb
{
    public ConcurrentDictionary<string, (HttpStatusCode Status, string Body, string? Location)> Pages { get; } = new(StringComparer.Ordinal);

    public void Page(string url, string body) => Pages[url] = (HttpStatusCode.OK, body, null);

    public void Redirect(string url, string location) => Pages[url] = (HttpStatusCode.Found, "", location);

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakeWeb web) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!web.Pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var page))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("") });
            var response = new HttpResponseMessage(page.Status) { RequestMessage = request, Content = new StringContent(page.Body) };
            if (page.Location is not null)
                response.Headers.Location = new Uri(page.Location);
            return Task.FromResult(response);
        }
    }
}
