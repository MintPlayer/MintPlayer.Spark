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
/// MCP PRD D19 follow-up (owner decision 2026-10-12): the two single-item metadata endpoints,
/// <c>POST /spark/queries/get</c> and <c>GET /spark/types/{id}</c>, refuse an anonymous caller the way
/// the access endpoints do since D17/D18 (<see cref="MintPlayer.Spark.Endpoints.SparkDenial"/>): a
/// uniform 401 on a host that can sign in, whether the query or type exists or not, while a signed-in
/// caller keeps the 404. The lists stay filtered and answer 200.
/// </summary>
/// <remarks>
/// Before the change both answered 404 to everyone, so an anonymous visitor who opened a protected
/// query or type was never told to sign in. The 401 is uniform (every not-found maps alike), so it opens
/// no existence oracle.
/// </remarks>
public class MetadataAnonymousDenialTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private static readonly Guid DocTypeId = Guid.Parse("d19a0000-1111-2222-3333-444455556666");
    private static readonly Guid AllDocsQueryId = Guid.Parse("d19a1111-1111-2222-3333-444455556666");
    private static readonly Guid UnknownId = Guid.Parse("d19a9999-1111-2222-3333-444455556666");

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

    private static Task<HttpResponseMessage> GetQueryAsync(HttpClient client, object queryId, bool signedIn = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/queries/get")
        {
            Content = JsonContent.Create(new { queryId = queryId.ToString() }),
        };
        if (signedIn)
            request.Headers.Add(StubHeader, "1");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, bool signedIn = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (signedIn)
            request.Headers.Add(StubHeader, "1");
        return client.SendAsync(request);
    }

    // ---- POST /spark/queries/get ------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_caller_on_a_protected_query_gets_401()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await GetQueryAsync(client, AllDocsQueryId);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous caller is told to sign in, as queries/execute tells it (D18)");
    }

    [Fact]
    public async Task Anonymous_caller_gets_the_same_401_for_an_unknown_query()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var real = await GetQueryAsync(client, AllDocsQueryId);
        using var unknown = await GetQueryAsync(client, UnknownId);

        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await unknown.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync(),
            "a differing body would tell an anonymous caller which query ids are real");
    }

    [Fact]
    public async Task Signed_in_caller_without_the_right_still_gets_404_for_a_query()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await GetQueryAsync(client, AllDocsQueryId, signedIn: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Host_with_no_way_to_sign_in_still_answers_404_for_a_query()
    {
        await using var factory = new SparkEndpointFactory<GuardedContext>(Store, [Model()], security: SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await GetQueryAsync(client, AllDocsQueryId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_caller_with_the_Query_right_gets_the_query()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty.Granting("Query/GuardedDoc"));
        using var client = factory.CreateClient();

        using var response = await GetQueryAsync(client, AllDocsQueryId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_query_list_stays_filtered_and_answers_200()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, "/spark/queries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("AllDocs");
    }

    // ---- GET /spark/types/{id} --------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_caller_on_a_protected_type_gets_401()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var real = await GetAsync(client, "/spark/types/GuardedDoc");
        using var unknown = await GetAsync(client, "/spark/types/NoSuchType");

        real.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await unknown.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync(),
            "a differing body would tell an anonymous caller which types are real");
    }

    [Fact]
    public async Task Signed_in_caller_without_the_right_still_gets_404_for_a_type()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, "/spark/types/GuardedDoc", signedIn: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_caller_with_the_right_gets_the_type_and_the_list_stays_200()
    {
        await using var factory = HostThatCanSignIn(SparkTestSecurity.Empty.Granting("Query/GuardedDoc"));
        using var client = factory.CreateClient();

        using var single = await GetAsync(client, "/spark/types/GuardedDoc");
        using var list = await GetAsync(client, "/spark/types");

        single.StatusCode.Should().Be(HttpStatusCode.OK);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
