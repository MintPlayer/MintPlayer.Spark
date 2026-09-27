using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CodeCoverage.Tests.Services;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Octokit.Internal;
using GraphQLConnection = Octokit.GraphQL.Connection;

namespace CodeCoverage.Tests;

/// <summary>
/// The one shared <see cref="IGitHubInstallationService"/> fake: real Octokit clients, REST and
/// GraphQL, talking to a <see cref="StubHttpMessageHandler"/> instead of GitHub.
/// </summary>
/// <remarks>
/// <para>
/// Octokit's response models have internal setters, so a fake that hands back substituted models
/// has to fabricate them through reflection or not at all — which is why every GitHub adapter in
/// this app was tested only through fakes of the adapter itself. Serving JSON instead lets the real
/// deserializer build the models, so a test exercises the adapter, Octokit's mapping, and the
/// status-code-to-exception translation (<see cref="NotFoundException"/>,
/// <see cref="ForbiddenException"/>) that the adapters branch on.
/// </para>
/// <para>
/// ⚠️ The canned bodies must be GitHub's real shapes — field names and nesting as the REST and
/// GraphQL APIs document them, since that is what Octokit binds to. A route that is not scripted
/// answers GitHub's own 404 body, so a test that forgot a route fails the way production would
/// rather than hanging or throwing something GitHub never sends.
/// </para>
/// </remarks>
internal sealed class StubGitHub : IGitHubInstallationService
{
    /// <summary>GitHub's body for a 404, verbatim.</summary>
    public const string NotFoundBody = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest","status":"404"}""";

    private readonly List<(HttpMethod Method, string Path, Func<Uri, string?, HttpResponseMessage> Respond)> routes = [];
    private readonly Queue<Func<string, string>> graphQLResponses = new();

    public StubGitHub()
    {
        Rest = new StubHttpMessageHandler(RespondRest);
        GraphQL = new StubHttpMessageHandler(RespondGraphQL);
    }

    /// <summary>The REST transport; its <c>Requests</c> record every call.</summary>
    public StubHttpMessageHandler Rest { get; }

    /// <summary>The GraphQL transport; its <c>Requests</c> record every query body.</summary>
    public StubHttpMessageHandler GraphQL { get; }

    /// <summary>Every REST request, as <c>METHOD /path?query</c>, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The installation ids clients were requested for, in order.</summary>
    public List<long> InstallationClients { get; } = [];

    /// <summary>How many app-level clients were requested.</summary>
    public int AppClients { get; private set; }

    /// <summary>When set, every client request throws this instead — the shape of a broken App credential.</summary>
    public Exception? Throws { get; set; }

    /// <summary>
    /// Answers <paramref name="method"/> on <paramref name="path"/> with a JSON body. A path with a
    /// <c>?</c> must match the query too; one without matches whatever query Octokit appends.
    /// </summary>
    public StubGitHub On(HttpMethod method, string path, HttpStatusCode status, string json)
        => On(method, path, (_, _) => StubHttpMessageHandler.Json(status, json));

    /// <summary>As above, with the request URI and body handed to <paramref name="respond"/>.</summary>
    public StubGitHub On(HttpMethod method, string path, Func<Uri, string?, HttpResponseMessage> respond)
    {
        routes.Add((method, path, respond));
        return this;
    }

    /// <summary>
    /// Queues the next GraphQL answer. The function receives the posted body (query and variables)
    /// and returns the JSON response, so a case can assert on what was asked as well as answer it.
    /// </summary>
    public StubGitHub OnGraphQL(Func<string, string> respond)
    {
        graphQLResponses.Enqueue(respond);
        return this;
    }

    public StubGitHub OnGraphQL(string json) => OnGraphQL(_ => json);

    /// <summary>The GraphQL bodies posted, in order.</summary>
    public IReadOnlyList<string> GraphQLBodies => [.. GraphQL.Requests.Select(r => r.Body ?? string.Empty)];

    public Task<IGitHubClient> CreateAppClientAsync()
    {
        AppClients++;
        if (Throws is not null) throw Throws;
        return Task.FromResult(Client());
    }

    public Task<IGitHubClient> CreateInstallationClientAsync(long installationId)
    {
        InstallationClients.Add(installationId);
        if (Throws is not null) throw Throws;
        return Task.FromResult(Client());
    }

    public Task<GraphQLConnection> CreateGraphQLConnectionAsync(long installationId, EClientType clientType)
    {
        InstallationClients.Add(installationId);
        if (Throws is not null) throw Throws;
        return Task.FromResult(new GraphQLConnection(
            new Octokit.GraphQL.ProductHeaderValue("coverage-tests"),
            new Octokit.GraphQL.Internal.InMemoryCredentialStore("ghs_test"),
            new HttpClient(GraphQL, disposeHandler: false)));
    }

    /// <summary>A REST client over the stub, exactly as Octokit builds one for api.github.com.</summary>
    public IGitHubClient Client() => new GitHubClient(new Connection(
        new Octokit.ProductHeaderValue("coverage-tests"),
        GitHubClient.GitHubApiUrl,
        new InMemoryCredentialStore(new Credentials("ghs_test")),
        new HttpClientAdapter(() => new NonDisposingHandler(Rest)),
        new SimpleJsonSerializer()));

    private HttpResponseMessage RespondRest(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!;
        Calls.Enqueue($"{request.Method} {uri.PathAndQuery}");
        foreach (var (method, path, respond) in routes)
        {
            var candidate = path.Contains('?') ? uri.PathAndQuery : uri.AbsolutePath;
            if (method == request.Method && string.Equals(path, candidate, StringComparison.Ordinal))
                return respond(uri, body);
        }
        return StubHttpMessageHandler.Json(HttpStatusCode.NotFound, NotFoundBody);
    }

    private HttpResponseMessage RespondGraphQL(HttpRequestMessage request, string? body)
    {
        if (graphQLResponses.Count == 0)
            throw new InvalidOperationException($"Unscripted GraphQL request: {body}");
        return StubHttpMessageHandler.Json(HttpStatusCode.OK, graphQLResponses.Dequeue()(body ?? string.Empty));
    }

    /// <summary>
    /// The query text of a posted GraphQL body, unwrapped from its JSON envelope, for assertions.
    /// </summary>
    public static string QueryOf(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("query").GetString() ?? string.Empty;

    /// <summary>
    /// Octokit's adapter disposes the handler it is given along with its HttpClient; the stub is
    /// shared by every client this fake hands out, so it is wrapped rather than handed over.
    /// </summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            // Deliberately does not dispose the inner handler.
        }
    }
}
