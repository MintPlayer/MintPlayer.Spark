using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Tls;

namespace SeoTester.Tests._Infrastructure;

/// <summary>DNS answered from tables (<see cref="ISeoDns"/>): nothing leaves the machine. A name without an entry has no records.</summary>
public sealed class FakeDns : ISeoDns
{
    public ConcurrentDictionary<string, string[]> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records other than TXT, keyed by <c>"{type}:{name}"</c> (<see cref="Key"/>).</summary>
    public ConcurrentDictionary<string, string[]> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Zone apexes by host; a host without one answers its last two labels.</summary>
    public ConcurrentDictionary<string, string> Apex { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names whose lookups fail (a resolver timeout).</summary>
    public ConcurrentDictionary<string, bool> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(SeoDnsType type, string name) => $"{type}:{name}";

    public void Set(SeoDnsType type, string name, params string[] values)
    {
        if (type == SeoDnsType.Txt)
            Txt[name] = values;
        else
            Records[Key(type, name)] = values;
    }

    public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(Txt.TryGetValue(name, out var records) ? records : []);

    public Task<IReadOnlyList<string>?> QueryAsync(string name, SeoDnsType type, CancellationToken cancellationToken)
    {
        if (Failing.ContainsKey(name))
            return Task.FromResult<IReadOnlyList<string>?>(null);
        if (type == SeoDnsType.Txt)
            return Task.FromResult<IReadOnlyList<string>?>(Txt.TryGetValue(name, out var txt) ? txt : []);
        return Task.FromResult<IReadOnlyList<string>?>(Records.TryGetValue(Key(type, name), out var values) ? values : []);
    }

    public Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken)
        => Task.FromResult<string?>(Apex.TryGetValue(host, out var apex) ? apex : string.Join('.', host.Split('.').TakeLast(2)));
}

/// <summary>One answer of <see cref="FakeWeb"/>.</summary>
public sealed record FakeResponse(HttpStatusCode Status, string Body, string? Location = null, string ContentType = "text/plain; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null, TimeSpan Delay = default);

/// <summary>A request <see cref="FakeWeb"/> received.</summary>
/// <param name="HeaderNames">The request's header names (never their values: one may be an API key).</param>
public sealed record FakeRequest(DateTime StartedUtc, DateTime EndedUtc, string Method, string Url, string? UserAgent, IReadOnlyList<string>? HeaderNames = null);

/// <summary>
/// The web as the app's clients see it: absolute URL → response; anything else is a 404. It replaces only the named
/// clients' primary handlers, so the SSRF-safe handler's redirect rules, caps and trace still run around it. It logs
/// every request (time, method, URL, user agent) and the most requests it had in flight at once per host.
/// </summary>
public sealed class FakeWeb
{
    private readonly ConcurrentDictionary<string, int> inFlight = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, FakeResponse> Pages { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<FakeRequest> Requests { get; } = new();

    /// <summary>The highest number of concurrent requests seen per host.</summary>
    public ConcurrentDictionary<string, int> MaxInFlight { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Page(string url, string body) => Pages[url] = new FakeResponse(HttpStatusCode.OK, body);

    public void Html(string url, string html, IReadOnlyDictionary<string, string>? headers = null, TimeSpan delay = default)
        => Pages[url] = new FakeResponse(HttpStatusCode.OK, html, ContentType: "text/html; charset=utf-8", Headers: headers, Delay: delay);

    public void Redirect(string url, string location, HttpStatusCode status = HttpStatusCode.Found) => Pages[url] = new FakeResponse(status, "", location);

    public IEnumerable<FakeRequest> RequestsTo(string host)
        => Requests.Where(r => string.Equals(new Uri(r.Url).Host, host, StringComparison.OrdinalIgnoreCase));

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakeWeb web) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            var host = request.RequestUri!.Host;
            var now = web.inFlight.AddOrUpdate(host, 1, (_, n) => n + 1);
            web.MaxInFlight.AddOrUpdate(host, now, (_, max) => Math.Max(max, now));
            try
            {
                if (!web.Pages.TryGetValue(request.RequestUri.AbsoluteUri, out var page))
                    page = new FakeResponse(HttpStatusCode.NotFound, "<html><body>Not found</body></html>", ContentType: "text/html");
                if (page.Delay > TimeSpan.Zero)
                    await Task.Delay(page.Delay, cancellationToken);
                var response = new HttpResponseMessage(page.Status) { RequestMessage = request };
                var body = request.Method == HttpMethod.Head ? [] : Encoding.UTF8.GetBytes(page.Body);
                response.Content = new ByteArrayContent(body);
                response.Content.Headers.TryAddWithoutValidation("Content-Type", page.ContentType);
                if (page.Location is not null)
                    response.Headers.Location = new Uri(page.Location, UriKind.RelativeOrAbsolute);
                foreach (var (name, value) in page.Headers ?? new Dictionary<string, string>())
                    if (!response.Headers.TryAddWithoutValidation(name, value))
                        response.Content.Headers.TryAddWithoutValidation(name, value);
                return response;
            }
            finally
            {
                web.inFlight.AddOrUpdate(host, 0, (_, n) => n - 1);
                web.Requests.Enqueue(new FakeRequest(started, DateTime.UtcNow, request.Method.Method, request.RequestUri.AbsoluteUri,
                    request.Headers.UserAgent.ToString(), request.Headers.Select(h => h.Key).ToList()));
            }
        }
    }
}

/// <summary>TLS answered from a table per host; a host without an entry has a good TLS 1.3 setup.</summary>
public sealed class FakeTls : ITlsProber
{
    public ConcurrentDictionary<string, TlsFacts> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static TlsFacts Good(DateTime notAfter) => new(true, null, "Tls13", "h2", notAfter, null, "R11 Let's Encrypt", LegacyTlsAnswer.Refused, LegacyTlsAnswer.Refused, true);

    public Task<TlsFacts> ProbeAsync(string host, int port, CancellationToken cancellationToken)
        => Task.FromResult(Hosts.TryGetValue(host, out var facts) ? facts : Good(DateTime.UtcNow.AddDays(80)));
}
