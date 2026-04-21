using System.Net.Http.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Convenience wrapper around an <see cref="HttpClient"/> that attaches the cached
/// antiforgery cookie + <c>X-XSRF-TOKEN</c> header to every mutating request. Obtain a
/// pre-configured instance via <see cref="SparkEndpointFactoryExtensions.CreateAuthorizedClientAsync"/>.
/// </summary>
public sealed class SparkTestClient : IDisposable
{
    private readonly HttpClient _inner;
    private readonly string _cookieHeader;
    private readonly string _xsrfToken;

    internal SparkTestClient(HttpClient inner, string cookieHeader, string xsrfToken)
    {
        _inner = inner;
        _cookieHeader = cookieHeader;
        _xsrfToken = xsrfToken;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => _inner.GetAsync(url);

    public Task<HttpResponseMessage> PostJsonAsync<T>(string url, T body) =>
        SendAsync(HttpMethod.Post, url, JsonContent.Create(body));

    public Task<HttpResponseMessage> PutJsonAsync<T>(string url, T body) =>
        SendAsync(HttpMethod.Put, url, JsonContent.Create(body));

    public Task<HttpResponseMessage> DeleteAsync(string url) =>
        SendAsync(HttpMethod.Delete, url, content: null);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);
        return await _inner.SendAsync(request);
    }

    public void Dispose() => _inner.Dispose();
}

public static class SparkEndpointFactoryExtensions
{
    /// <summary>
    /// Returns a <see cref="SparkTestClient"/> that has already done a warmup GET to mint
    /// antiforgery tokens and will attach them to every Post/Put/Delete request.
    /// </summary>
    public static async Task<SparkTestClient> CreateAuthorizedClientAsync<TContext>(
        this SparkEndpointFactory<TContext> factory)
        where TContext : SparkContext
    {
        var (cookieHeader, xsrfToken) = await factory.MintAntiforgeryAsync();
        return new SparkTestClient(factory.CreateClient(), cookieHeader, xsrfToken);
    }
}
