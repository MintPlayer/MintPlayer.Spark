using System.Net;
using Octokit;
using Octokit.Internal;

namespace MintPlayer.Spark.Webhooks.GitHub.Services.Internal;

/// <summary>
/// Octokit REST <see cref="IHttpClient"/> decorator that transparently retries once on a 401
/// response: invalidate the cached installation token, mint a fresh one, rewrite the
/// Authorization header on the original request, retry. Exactly one retry per failed request.
/// </summary>
internal sealed class TokenRefreshingHttpClient : IHttpClient
{
    private readonly IHttpClient _inner;
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public TokenRefreshingHttpClient(IHttpClient inner, long installationId, GitHubInstallationService service)
    {
        _inner = inner;
        _installationId = installationId;
        _service = service;
    }

    public async Task<IResponse> Send(IRequest request, CancellationToken cancellationToken, Func<object, object> preprocessResponseBody)
    {
        var response = await _inner.Send(request, cancellationToken, preprocessResponseBody);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        _service.InvalidateInstallation(_installationId);
        var fresh = await _service.GetOrCreateInstallationTokenAsync(_installationId, cancellationToken);
        // Align with Octokit's Credentials serialization ("Token xxx" with capital T) so
        // WireMock scenarios / logs / packet captures see a single consistent header shape
        // across the initial call and the retry.
        request.Headers["Authorization"] = $"Token {fresh.Token}";
        return await _inner.Send(request, cancellationToken, preprocessResponseBody);
    }

    public void SetRequestTimeout(TimeSpan timeout) => _inner.SetRequestTimeout(timeout);

    public void Dispose() => _inner.Dispose();
}
