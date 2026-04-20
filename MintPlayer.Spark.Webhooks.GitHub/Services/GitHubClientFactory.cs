using MintPlayer.SourceGenerators.Attributes;
using Octokit;
using Octokit.Internal;
using GraphQLConnection = Octokit.GraphQL.Connection;
using GraphQLCredentialStore = Octokit.GraphQL.ICredentialStore;
using GraphQLProductHeaderValue = Octokit.GraphQL.ProductHeaderValue;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

/// <summary>
/// Default <see cref="IGitHubClientFactory"/> targeting api.github.com.
/// Tests inject an alternative factory pointing at a local WireMock server.
/// </summary>
[Register(typeof(IGitHubClientFactory), ServiceLifetime.Singleton)]
internal sealed class GitHubClientFactory : IGitHubClientFactory
{
    private static readonly ProductHeaderValue ProductHeader = new("SparkWebhooks", "1.0");
    private static readonly GraphQLProductHeaderValue GraphQLProductHeader = new("SparkWebhooks", "1.0");

    public IGitHubClient CreateAppClient(string jwt) =>
        new GitHubClient(ProductHeader)
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer),
        };

    public IGitHubClient CreateInstallationClient(IHttpClient refreshingHttpClient, ICredentialStore credentialStore)
    {
        var connection = new Connection(
            ProductHeader,
            GitHubClient.GitHubApiUrl,
            credentialStore,
            refreshingHttpClient,
            new SimpleJsonSerializer());
        return new GitHubClient(connection);
    }

    public GraphQLConnection CreateAppGraphQLConnection(string appToken) =>
        new(GraphQLProductHeader, appToken);

    public GraphQLConnection CreateInstallationGraphQLConnection(
        GraphQLCredentialStore credentialStore,
        HttpClient httpClient) =>
        new(GraphQLProductHeader, credentialStore, httpClient);
}
