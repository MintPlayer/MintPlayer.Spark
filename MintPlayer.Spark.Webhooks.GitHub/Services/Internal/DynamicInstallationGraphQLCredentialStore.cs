using Octokit.GraphQL;

namespace MintPlayer.Spark.Webhooks.GitHub.Services.Internal;

/// <summary>
/// Octokit.GraphQL credential store that fetches the current cached installation token from
/// <see cref="GitHubInstallationService"/> on every request. This lets a cached
/// <see cref="Connection"/> survive token refreshes — the next request automatically
/// picks up the freshly minted token.
/// </summary>
internal sealed class DynamicInstallationGraphQLCredentialStore : ICredentialStore
{
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public DynamicInstallationGraphQLCredentialStore(long installationId, GitHubInstallationService service)
    {
        _installationId = installationId;
        _service = service;
    }

    public async Task<string> GetCredentials(CancellationToken cancellationToken)
    {
        var token = await _service.GetOrCreateInstallationTokenAsync(_installationId, cancellationToken);
        return token.Token;
    }
}
