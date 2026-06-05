using System.Net;
using System.Net.Http.Headers;

namespace MintPlayer.Spark.Webhooks.GitHub.Services.Internal;

/// <summary>
/// .NET <see cref="DelegatingHandler"/> for the GraphQL <see cref="System.Net.Http.HttpClient"/>
/// that transparently retries once on a 401 response: invalidate the cached installation token,
/// mint a fresh one, rewrite the Authorization header on the original request, retry.
/// Exactly one retry per failed request.
/// </summary>
internal sealed class TokenRefreshingHandler : DelegatingHandler
{
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public TokenRefreshingHandler(long installationId, GitHubInstallationService service)
    {
        _installationId = installationId;
        _service = service;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        _service.InvalidateInstallation(_installationId);
        var fresh = await _service.GetOrCreateInstallationTokenAsync(_installationId, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
