using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce.Tests.Payments;

/// <summary>The doc-derived fixtures (Payments/Fixtures, copied to the output). See Fixtures/README.md.</summary>
internal static class Fixtures
{
    public static string Text(string provider, string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payments", "Fixtures", provider, name));

    public static JsonElement Json(string provider, string name)
    {
        using var document = JsonDocument.Parse(Text(provider, name));
        return document.RootElement.Clone();
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// Serves canned provider responses by "METHOD path-prefix" and records every request (method, URL, headers, body), so a
/// test can assert what an adapter sent without any network.
/// </summary>
internal sealed class FakeProviderHandler : HttpMessageHandler
{
    private readonly List<(string Method, string PathPrefix, HttpStatusCode Status, string Body)> routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public FakeProviderHandler On(string method, string pathPrefix, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        routes.Add((method, pathPrefix, status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body));
        var path = request.RequestUri!.PathAndQuery;
        // The longest matching prefix wins, so "/v2/payments/tr_1/refunds" beats "/v2/payments/tr_1".
        var route = routes.Where(r => r.Method == request.Method.Method && path.StartsWith(r.PathPrefix, StringComparison.Ordinal))
            .OrderByDescending(r => r.PathPrefix.Length)
            .FirstOrDefault();
        if (route.Body is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"status\":404,\"title\":\"Not Found\"}", Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
    }
}

internal sealed record RecordedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>An <see cref="IHttpClientFactory"/> that always hands out a client over one handler.</summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
