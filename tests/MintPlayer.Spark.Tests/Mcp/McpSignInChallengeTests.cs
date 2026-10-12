using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// With an OAuth setup wired (MCP PRD D17, spike S11, plan M3b): RFC 9728 metadata, the
/// <c>WWW-Authenticate: Bearer resource_metadata=…, scope=…</c> challenge, and an anonymous refusal answered as a
/// real HTTP 401 whose JSON-RPC error tells the agent how to start its client's sign-in, uniformly for exposed,
/// unexposed and unknown targets. Without OAuth the refusal stays a tool error (covered by <see cref="McpServerTests"/>).
/// </summary>
public class McpSignInChallengeTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string MetadataUrl = "https://acme.example/.well-known/oauth-protected-resource/mcp";
    private const string ServerIssuer = "https://id.acme.example";

    private SparkEndpointFactory<GuardedContext> Create(SparkTestSecurity security, Action<MintPlayer.Spark.Mcp.SparkMcpOptions>? configure = null)
        => McpTestHost.Create(Store, security, configure,
            configureSpark: spark => spark.Services.AddSingleton<IAgentCredentialScheme, StubOAuthScheme>());

    [Fact]
    public async Task The_protected_resource_metadata_names_the_resource_its_server_and_scopes()
    {
        await using var factory = Create(SparkTestSecurity.Empty);
        var client = factory.CreateClient();

        foreach (var path in new[] { "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource" })
        {
            var document = await client.GetFromJsonAsync<JsonObject>(path);
            document!["resource"]!.GetValue<string>().Should().Be(McpTestHost.CanonicalUrl, "the canonical URL, never the Host header (D20)");
            document["authorization_servers"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal(ServerIssuer);
            document["scopes_supported"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal("mcp:read", "mcp:write");
            document["bearer_methods_supported"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal("header");
        }
    }

    [Fact]
    public async Task Personal_tokens_alone_publish_no_metadata_and_challenge_plainly()
    {
        await using var factory = McpTestHost.Create(Store, SparkTestSecurity.Empty, mcp => mcp.RequireSignIn = true);
        var client = factory.CreateClient();

        (await client.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "with no OAuth setup there is nowhere to sign in (PRD §4.2, setup 1)");
        var reply = await InitializeAsync(client);
        reply.Status.Should().Be(HttpStatusCode.Unauthorized);
        reply.Response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
    }

    [Fact]
    public async Task Anonymous_refusals_are_one_uniform_http_401_with_the_challenge()
    {
        await SeedAsync();
        await using var factory = Create(SparkTestSecurity.Empty);
        var client = factory.CreateClient();

        var answers = new[]
        {
            await CallAsync(client, "acme_get", new { type = "GuardedDoc", id = "GuardedDocs/challenge-visible" }),
            await CallAsync(client, "acme_get", new { type = "GuardedDoc", id = "GuardedDocs/missing" }),
            await CallAsync(client, "acme_get", new { type = "FilteredDoc", id = "FilteredDocs/x" }),
            await CallAsync(client, "acme_get", new { type = "NoSuchType", id = "x/1" }),
            await CallAsync(client, "acme_describe", new { type = "GuardedDoc" }),
            await CallAsync(client, "acme_describe", new { type = "NoSuchType" }),
            await CallAsync(client, "acme_query", new { query = "alldocs" }),
            await CallAsync(client, "acme_query", new { query = "nosuchquery" }),
        };

        answers.Should().AllSatisfy(a =>
        {
            a.Status.Should().Be(HttpStatusCode.Unauthorized, "an HTTP 401 is what makes an agent offer its sign-in (S11)");
            a.Response.Headers.WwwAuthenticate.ToString().Should().Be($"Bearer resource_metadata=\"{MetadataUrl}\", scope=\"mcp:read mcp:write\"");
            a.Body!["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
            a.Body["id"].Should().NotBeNull("the error names the refused call");
            a.Body["result"].Should().BeNull();
        });
        answers.Select(a => a.ErrorMessage).Distinct().Should().ContainSingle(
                "an exposed type, an unexposed one and a name that does not exist must be indistinguishable")
            .Which.Should().StartWith("401 Unauthorized").And.Contain("claude mcp login").And.Contain("codex mcp login").And.Contain("/mcp auth");
        answers.Select(a => a.Body!["error"]!["code"]!.GetValue<int>()).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task What_anonymous_may_read_still_answers_200()
    {
        await SeedAsync();
        await using var factory = Create(SparkTestSecurity.Empty.Granting("Query/GuardedDoc", "Read/GuardedDoc"));

        var reply = await CallAsync(factory.CreateClient(), "acme_get", new { type = "GuardedDoc", id = "GuardedDocs/challenge-visible" });

        reply.Status.Should().Be(HttpStatusCode.OK, "only a refusal turns into a 401; anonymous use keeps working (D17)");
        reply.IsToolError.Should().BeFalse(reply.ToolText);
    }

    [Fact]
    public async Task RequireSignIn_answers_initialize_with_the_challenge_and_a_JSON_RPC_error()
    {
        await using var factory = Create(SparkTestSecurity.Empty, mcp => mcp.RequireSignIn = true);

        var reply = await InitializeAsync(factory.CreateClient());

        reply.Status.Should().Be(HttpStatusCode.Unauthorized);
        reply.Response.Headers.WwwAuthenticate.ToString().Should().Contain($"resource_metadata=\"{MetadataUrl}\"");
        reply.Body!["id"].Should().NotBeNull("the gate reads the call's id from the body it has not handed to the SDK yet");
        reply.ErrorMessage.Should().StartWith("401 Unauthorized");
    }

    [Fact]
    public async Task A_refused_token_gets_invalid_token_and_the_metadata_pointer()
    {
        await using var factory = Create(SparkTestSecurity.Empty);

        var reply = await ListToolsAsync(factory.CreateClient(), r => r.Headers.Authorization = new("Bearer", "not-a-token-of-ours"));

        reply.Status.Should().Be(HttpStatusCode.Unauthorized);
        reply.Response.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer error=\"invalid_token\", resource_metadata=");
    }

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new GuardedDoc { Name = "alpha", IsVisible = true }, "GuardedDocs/challenge-visible");
        await session.SaveChangesAsync();
    }

    /// <summary>An embedded-server registration on the stub bearer: what idp.ForMcp() registers, without the IdP.</summary>
    private sealed class StubOAuthScheme : IAgentCredentialScheme
    {
        public EAgentAuthentication Setup => EAgentAuthentication.EmbeddedAuthorizationServer;
        public string Scheme => McpTestHost.BearerScheme;
        public bool SubjectIsLocalUserId => true;
        public IReadOnlyList<string> ScopesSupported => ["mcp:read", "mcp:write"];
        public string? AuthorizationServer(string requestOrigin) => ServerIssuer;

        public ValueTask<AgentCredential?> DescribeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentCredential?>(new(
                new AgentInformation(EAgentKind.MetadataDocument, "https://agent.example/client.json", "agent.example", null, principal),
                new AgentGrantScope("AgentGrants/stub", EAgentPermission.Read, Types: null)));
    }
}
