using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// MCP PRD D18: <c>POST /spark/queries/execute</c> refuses an anonymous caller the way every access
/// endpoint does (<see cref="MintPlayer.Spark.Endpoints.SparkDenial"/>) — a uniform 401 on a host that
/// can sign in, whether the query exists or not — while a signed-in caller keeps the 404 it always got.
/// </summary>
/// <remarks>
/// Before D18 the execute endpoint answered 404 to everyone, so an anonymous visitor on a protected
/// query was never told to sign in. The 401 is uniform (the gate runs before the query is resolved
/// into rows), so it opens no existence oracle: a real query and an unknown id get the same answer.
/// </remarks>
public class QueryExecuteAnonymousDenialTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private static readonly Guid DocTypeId = Guid.Parse("d18d0000-1111-2222-3333-444455556666");
    private static readonly Guid AllDocsQueryId = Guid.Parse("d18d1111-1111-2222-3333-444455556666");

    private const string StubScheme = "Stub";
    private const string StubHeader = "X-Stub-User";

    /// <summary>Authenticates a caller who sends the header; anyone else arrives anonymous.</summary>
    private sealed class StubUserHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(StubHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], StubScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), StubScheme)));
        }
    }

    private static EntityTypeFile Model()
    {
        var model = GuardedDocModel.For(DocTypeId);
        model.Queries =
        [
            new SparkQuery
            {
                Id = AllDocsQueryId,
                Name = "AllDocs",
                Alias = "alldocs",
                Source = "Database.Docs",
                EntityType = "GuardedDoc",
            },
        ];
        return model;
    }

    private SparkEndpointFactory<GuardedContext> HostThatCanSignIn(SparkTestSecurity security) => new(
        Store,
        [Model()],
        configureSpark: spark => spark.AddCredentialScheme<AuthenticationSchemeOptions, StubUserHandler>(StubScheme),
        security: security);

    private static async Task<HttpResponseMessage> ExecuteAsync(HttpClient client, object queryId, bool signedIn = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/queries/execute")
        {
            Content = JsonContent.Create(Wire.Query(queryId)),
        };
        if (signedIn)
            request.Headers.Add(StubHeader, "1");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Anonymous_caller_on_a_protected_query_gets_401()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await ExecuteAsync(client, AllDocsQueryId);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous caller is told to sign in, as /spark/po/* tells it (D18)");
    }

    [Fact]
    public async Task Anonymous_caller_gets_the_same_401_for_an_unknown_query()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var real = await ExecuteAsync(client, AllDocsQueryId);
        using var unknown = await ExecuteAsync(client, Guid.Parse("d18d9999-1111-2222-3333-444455556666"));

        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await unknown.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync(),
            "a differing body would tell an anonymous caller which query ids are real");
    }

    [Fact]
    public async Task Signed_in_caller_without_the_right_still_gets_404()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await ExecuteAsync(client, AllDocsQueryId, signedIn: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "signing in would not help a signed-in caller, so the refusal stays a not-found");
    }

    [Fact]
    public async Task Host_with_no_way_to_sign_in_still_answers_404()
    {
        await using var factory = new SparkEndpointFactory<GuardedContext>(Store, [Model()], security: SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await ExecuteAsync(client, AllDocsQueryId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "telling a caller to authenticate where nothing can authenticate them would be false");
    }

    /// <summary>The control: the same anonymous caller with the right runs the query.</summary>
    [Fact]
    public async Task Anonymous_caller_with_the_Query_right_gets_200()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty.Granting("Query/GuardedDoc"));
        using var client = factory.CreateClient();

        using var response = await ExecuteAsync(client, AllDocsQueryId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
