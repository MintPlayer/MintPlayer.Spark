using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Octokit.Internal;
using GraphQLConnection = Octokit.GraphQL.Connection;
using GraphQLCredentialStore = Octokit.GraphQL.ICredentialStore;
using GraphQLProductHeaderValue = Octokit.GraphQL.ProductHeaderValue;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub._Infrastructure;

/// <summary>
/// Test-only factory that points Octokit at a locally running WireMock server.
/// Enables end-to-end token refresh + 401 retry tests without hitting api.github.com.
/// </summary>
internal sealed class WireMockGitHubClientFactory(Uri restBaseAddress) : IGitHubClientFactory
{
    private static readonly ProductHeaderValue ProductHeader = new("SparkWebhooks", "test");
    private static readonly GraphQLProductHeaderValue GraphQLProductHeader = new("SparkWebhooks", "test");

    public IGitHubClient CreateAppClient(string jwt) =>
        new GitHubClient(ProductHeader, restBaseAddress)
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer),
        };

    public IGitHubClient CreateInstallationClient(IHttpClient refreshingHttpClient, ICredentialStore credentialStore)
    {
        var connection = new Connection(
            ProductHeader,
            restBaseAddress,
            credentialStore,
            refreshingHttpClient,
            new SimpleJsonSerializer());
        return new GitHubClient(connection);
    }

    public GraphQLConnection CreateAppGraphQLConnection(string appToken) =>
        new(GraphQLProductHeader, appToken);

    public GraphQLConnection CreateInstallationGraphQLConnection(GraphQLCredentialStore credentialStore, HttpClient httpClient) =>
        new(GraphQLProductHeader, credentialStore, httpClient);
}
