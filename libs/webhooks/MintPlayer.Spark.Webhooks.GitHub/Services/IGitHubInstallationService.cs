using Octokit;
using GraphQLConnection = Octokit.GraphQL.Connection;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

public interface IGitHubInstallationService
{
    Task<IGitHubClient> CreateAppClientAsync();
    Task<IGitHubClient> CreateInstallationClientAsync(long installationId);
    Task<GraphQLConnection> CreateGraphQLConnectionAsync(long installationId, EClientType clientType);

    /// <summary>
    /// The raw installation access token, for a consumer that is not an Octokit client: a
    /// <c>git</c> process authenticating with <c>x-access-token</c>. Served from the same cache as the
    /// clients above, so it has more than 60 seconds left.
    /// </summary>
    /// <remarks>
    /// ⚠️ A bearer credential for everything the installation may touch. Never log it, never store
    /// it, never put it on a command line; hand it to the one process that needs it and let it go.
    /// <para>
    /// Implemented by default as a refusal so test doubles of this interface keep compiling; the
    /// production implementation overrides it.
    /// </para>
    /// </remarks>
    Task<string> GetInstallationTokenAsync(long installationId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not mint raw installation tokens.");
}
