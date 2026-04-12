using Octokit;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

public interface IGitHubInstallationService
{
    Task<IGitHubClient> CreateAppClientAsync();
    Task<IGitHubClient> CreateInstallationClientAsync(long installationId);
}
