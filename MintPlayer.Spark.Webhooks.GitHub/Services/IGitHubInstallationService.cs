using Octokit;
using GraphQLConnection = Octokit.GraphQL.Connection;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

public interface IGitHubInstallationService
{
    Task<IGitHubClient> CreateAppClientAsync();
    Task<IGitHubClient> CreateInstallationClientAsync(long installationId);
    Task<GraphQLConnection> CreateGraphQLConnectionAsync(long installationId, EClientType clientType);
}
