using Octokit;

namespace MintPlayer.Spark.Webhooks.GitHub.Services.Internal;

/// <summary>
/// Octokit REST credential store that fetches the current cached installation token from
/// <see cref="GitHubInstallationService"/> on every request. This lets a cached
/// <see cref="IGitHubClient"/> survive token refreshes — the next request automatically
/// picks up the freshly minted token.
/// </summary>
internal sealed class DynamicInstallationCredentialStore : ICredentialStore
{
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public DynamicInstallationCredentialStore(long installationId, GitHubInstallationService service)
    {
        _installationId = installationId;
        _service = service;
    }

    public async Task<Credentials> GetCredentials()
    {
        var token = await _service.GetOrCreateInstallationTokenAsync(_installationId, CancellationToken.None);
        return new Credentials(token.Token);
    }
}
