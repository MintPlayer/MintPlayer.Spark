using Octokit;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

public interface IGitHubInstallationService
{
    Task<IGitHubClient> CreateClientAsync(long installationId);
}
