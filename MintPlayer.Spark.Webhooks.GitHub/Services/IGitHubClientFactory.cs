using Octokit;
using Octokit.Internal;
using GraphQLConnection = Octokit.GraphQL.Connection;
using GraphQLCredentialStore = Octokit.GraphQL.ICredentialStore;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

/// <summary>
/// Seam for constructing Octokit clients / connections. Introduced so tests can redirect
/// all GitHub API traffic to a WireMock server; the production implementation targets
/// api.github.com.
/// </summary>
public interface IGitHubClientFactory
{
    /// <summary>Creates a JWT-bearing App client.</summary>
    IGitHubClient CreateAppClient(string jwt);

    /// <summary>
    /// Creates an installation client whose REST pipeline uses the given
    /// <paramref name="refreshingHttpClient"/> (decorates Octokit's default with
    /// a 401-retry + token-refresh layer) and <paramref name="credentialStore"/>
    /// (returns the currently cached installation token on each request).
    /// </summary>
    IGitHubClient CreateInstallationClient(IHttpClient refreshingHttpClient, ICredentialStore credentialStore);

    /// <summary>Creates a JWT-bearing App GraphQL connection.</summary>
    GraphQLConnection CreateAppGraphQLConnection(string appToken);

    /// <summary>
    /// Creates an installation GraphQL connection whose HTTP pipeline uses the given
    /// <paramref name="httpClient"/> (token-refreshing <see cref="HttpClient"/>) and
    /// <paramref name="credentialStore"/>.
    /// </summary>
    GraphQLConnection CreateInstallationGraphQLConnection(GraphQLCredentialStore credentialStore, HttpClient httpClient);
}
