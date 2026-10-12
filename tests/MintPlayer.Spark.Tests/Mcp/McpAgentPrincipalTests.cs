using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// An agent's principal carries the local user id as <see cref="ClaimTypes.NameIdentifier"/> in every setup (found wiring
/// Fleet, 2026-10-12): application code reads the id from that claim as it does from the sign-in cookie, and Fleet's car
/// row filter treated an authenticated principal without it as a machine, unrestricted, so an agent saw every user's cars.
/// <c>ISparkCurrentUser.Id</c> already mapped the token's <c>sub</c> (M1); code that reads the claim did not.
/// </summary>
public class McpAgentPrincipalTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private string Name(string localPart) => $"{localPart}-{Scope}";

    private static Task<Reply> NameIdAsync(HttpClient client, string token)
        => CallAsync(client, "acme_nameid", new { }, McpAgentClient.Bearer(token));

    [Fact]
    public async Task A_personal_access_token()
    {
        await using var factory = McpAccountHost.Create(Store, mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(factory, Name("pat"));
        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("pat"));
        var (token, _) = await browser.CreateTokenAsync("probe");

        (await NameIdAsync(factory.CreateClient(), token)).ToolText.Should().Contain("nameid=" + user.Id);
    }

    [Fact]
    public async Task An_embedded_servers_token()
    {
        var clientId = $"https://agent.example/{Scope}/probe.json";
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        await using var factory = McpEmbeddedHost.Create(Store, documents, mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(factory, Name("embedded"));
        var agent = new McpAgentClient(factory.CreateClient(), clientId, "http://127.0.0.1:53682/callback");
        var tokens = await agent.SignInAndRedeemAsync(user.Email!);

        (await NameIdAsync(factory.CreateClient(), tokens["access_token"]!.GetValue<string>())).ToolText.Should().Contain("nameid=" + user.Id);
    }

    [Fact]
    public async Task An_external_servers_token_carries_the_local_id_never_the_external_subject()
    {
        var issuer = new TestIssuer();
        await using var factory = McpExternalHost.Create(Store, issuer, configureMcp: mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(factory, Name("external"));
        await McpExternalHost.LinkAsync(factory, Store, user.Id!, McpExternalHost.Provider, "corp-" + Name("external"));

        var reply = await NameIdAsync(factory.CreateClient(), issuer.Token("corp-" + Name("external")));

        reply.Status.Should().Be(HttpStatusCode.OK);
        reply.ToolText.Should().Contain("nameid=" + user.Id);
    }
}

/// <summary>Reads the caller's id the way application code does: the <see cref="ClaimTypes.NameIdentifier"/> claim.</summary>
[McpServerToolType]
public sealed class ClaimsProbeTools(IHttpContextAccessor accessor)
{
    [McpServerTool(Name = "nameid", ReadOnly = true)]
    public CallToolResult NameId()
        => MintPlayer.Spark.Mcp.SparkMcpResult.Text("nameid=" + accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier));
}
