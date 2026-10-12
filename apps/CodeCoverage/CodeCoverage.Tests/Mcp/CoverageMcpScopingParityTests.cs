using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.Controllers;
using CodeCoverage.Tests.Mcp._Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeCoverage.Tests.Mcp;

/// <summary>
/// The MCP tools see exactly the repositories the browse API shows the same caller (MCP PRD §4.8.2, D9, plan M6), and a
/// private repository's existence never leaks through them (#453, D17): for an anonymous caller every hidden or missing
/// repository is one HTTP 401 with the sign-in challenge, for a signed-in caller one "Not found.", byte for byte.
/// </summary>
/// <remarks>
/// Both surfaces run in the real application. The MCP side is a real <c>/mcp</c> call with a real personal access token;
/// the browse side is HTTP for anonymous callers and, for a signed-in one, the controller under a cookie principal inside
/// the host (this host has no sign-in a test can drive: GitHub OAuth only). Only GitHub's network edge is stubbed.
/// </remarks>
[Collection(CoverageMcpHostCollection.Name)]
public class CoverageMcpScopingParityTests(CoverageMcpHostFixture fixture)
{
    /// <summary>The repository-scoped tools, each with arguments that are valid for any repository.</summary>
    public static TheoryData<string, string> Tools => new()
    {
        { "codecoverage_summary", """{}""" },
        { "codecoverage_hotspots", """{}""" },
        { "codecoverage_file", """{"path":"src/secret.ts"}""" },
        { "codecoverage_patch", """{"pullRequest":7}""" },
        { "codecoverage_drops", """{}""" },
    };

    /// <summary>Every way a prober might name something they may not see. The first three exist.</summary>
    private static readonly (string Mcp, string Browse)[] Hidden =
    [
        ("github/acme/secret", "github/acme/secret"),               // private, live name
        ("github/acme/old-secret", "github/acme/old-secret"),       // private, a name it used to have
        ("github/other-org/vault", "github/other-org/vault"),       // another organisation's private repository
        ("github/acme/made-up", "github/acme/made-up"),             // missing, under a known owner
        ("github/stranger/anything", "github/stranger/anything"),   // unknown owner
        ("nosuchforge/acme/secret", "nosuchforge/acme/secret"),     // a forge this deployment does not have
    ];

    private const string Missing = "github/acme/made-up";

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Anonymous_every_hidden_or_missing_repository_is_the_same_401_on_mcp_and_on_browse(string tool, string extra)
    {
        using var client = Client();
        var reference = await CallAsync(client, tool, Missing, extra, token: null);
        reference.Status.Should().Be(HttpStatusCode.Unauthorized, "an anonymous refusal is an HTTP 401 when OAuth is wired (D17)");
        reference.Response.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer resource_metadata=");

        var browseReference = await BrowseAnonymousAsync(client, Missing);
        browseReference.Should().Be("""401 {"error":"Authentication required"}""");

        foreach (var (mcp, browse) in Hidden)
        {
            var reply = await CallAsync(client, tool, mcp, extra, token: null);
            reply.Status.Should().Be(reference.Status, mcp);
            reply.Text.Should().Be(reference.Text, $"{mcp} must be indistinguishable from a repository that does not exist");
            reply.Response.Headers.WwwAuthenticate.ToString().Should().Be(reference.Response.Headers.WwwAuthenticate.ToString());

            (await BrowseAnonymousAsync(client, browse)).Should().Be(browseReference, browse);
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Signed_in_without_access_every_hidden_or_missing_repository_is_the_same_not_found(string tool, string extra)
    {
        using var client = Client();
        var token = fixture.Credentials.OutsiderToken;
        var reference = await CallAsync(client, tool, Missing, extra, token);
        reference.Status.Should().Be(HttpStatusCode.OK);
        reference.IsToolError.Should().BeTrue();
        reference.ToolText.Should().Be("Not found.");

        var browseReference = await BrowseSignedInAsync(CoverageMcpSeed.OutsiderUserId, Missing);
        browseReference.Should().Be("""404 {"error":"Not found"}""");

        foreach (var (mcp, browse) in Hidden)
        {
            (await CallAsync(client, tool, mcp, extra, token)).Text.Should().Be(reference.Text, $"{mcp} must be indistinguishable from a repository that does not exist");
            (await BrowseSignedInAsync(CoverageMcpSeed.OutsiderUserId, browse)).Should().Be(browseReference, browse);
        }
    }

    [Fact]
    public async Task A_public_repository_works_anonymously_on_both()
    {
        using var client = Client();

        var reply = await CallAsync(client, "codecoverage_summary", "github/acme/open", "{}", token: null);

        reply.Status.Should().Be(HttpStatusCode.OK, reply.Text);
        reply.IsToolError.Should().BeFalse(reply.ToolText);
        reply.Data["repository"]!.GetValue<string>().Should().Be("github/acme/open");
        reply.Data["latest"]!["sha"]!.GetValue<string>().Should().Be(CoverageMcpSeed.OpenHead, "the default branch reads the commit the website's headline shows");
        (await BrowseAnonymousAsync(client, "github/acme/open")).Should().StartWith("200 ");
    }

    [Fact]
    public async Task The_signed_in_caller_sees_the_private_repository_of_an_owner_they_have_access_to_on_both()
    {
        using var client = Client();
        var token = fixture.Credentials.DevToken;

        foreach (var name in new[] { "github/acme/secret", "github/acme/old-secret" })
        {
            var reply = await CallAsync(client, "codecoverage_summary", name, "{}", token);

            reply.IsToolError.Should().BeFalse($"acme-dev's GitHub installations reach acme, so {name} is visible: {reply.ToolText}");
            reply.Data["repository"]!.GetValue<string>().Should().Be("github/acme/secret", "a remembered name resolves to the repository it became");
            reply.Data["latest"]!["sha"]!.GetValue<string>().Should().Be(CoverageMcpSeed.SecretHead);
            (await BrowseSignedInAsync(CoverageMcpSeed.DevUserId, name)).Should().StartWith("200 ");
        }

        // Another organisation's private repository stays invisible to them, alike on both.
        (await CallAsync(client, "codecoverage_summary", "github/other-org/vault", "{}", token)).ToolText.Should().Be("Not found.");
        (await BrowseSignedInAsync(CoverageMcpSeed.DevUserId, "github/other-org/vault")).Should().Be("""404 {"error":"Not found"}""");
    }

    [Fact]
    public async Task The_repository_list_is_the_browse_listing_for_every_caller()
    {
        using var client = Client();

        var anonymous = await ListAsync(client, token: null);
        var outsider = await ListAsync(client, fixture.Credentials.OutsiderToken);
        var dev = await ListAsync(client, fixture.Credentials.DevToken);

        anonymous.Should().Equal("github/acme/open", "github/acme/sprawl");
        outsider.Should().Equal(anonymous, "a signed-in caller without access sees what a visitor sees");
        dev.Should().Equal("github/acme/open", "github/acme/secret", "github/acme/sprawl");

        // The account page lists the same repositories of acme for each of them.
        (await BrowseAccountAnonymousAsync(client, "acme")).Should().BeEquivalentTo(anonymous.Select(Name));
        (await BrowseAccountSignedInAsync(CoverageMcpSeed.DevUserId, "acme")).Should().BeEquivalentTo(dev.Select(Name));
        (await BrowseAccountSignedInAsync(CoverageMcpSeed.OutsiderUserId, "acme")).Should().BeEquivalentTo(outsider.Select(Name));
    }

    [Fact]
    public async Task A_grant_without_the_type_refuses_the_tool_like_a_missing_repository()
    {
        using var client = Client();

        // The user allowed this token Commit only: the agent layers in IPermissionService narrow the app's own tools too (D13).
        var reply = await McpWire.CallAsync(client, "codecoverage_list_repositories", new { }, McpWire.Bearer(fixture.Credentials.DevCommitsOnlyToken));

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Be("Not found.");
        (await McpWire.CallAsync(client, "codecoverage_list_repositories", new { }, McpWire.Bearer(fixture.Credentials.DevToken))).IsToolError.Should().BeFalse();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private HttpClient Client() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A tool call with a fixed JSON-RPC id, so two refusals can be compared byte for byte.</summary>
    private static Task<McpWire.Reply> CallAsync(HttpClient client, string tool, string repository, string extra, string? token)
    {
        var arguments = JsonNode.Parse(extra)!.AsObject();
        arguments["repository"] = repository;
        return McpWire.SendAsync(client, "tools/call", new { name = tool, arguments }, token is null ? null : McpWire.Bearer(token), id: 1);
    }

    private static async Task<IReadOnlyList<string>> ListAsync(HttpClient client, string? token)
    {
        var reply = await McpWire.CallAsync(client, "codecoverage_list_repositories", new { take = 50 }, token is null ? null : McpWire.Bearer(token));
        reply.IsToolError.Should().BeFalse(reply.ToolText);
        return [.. reply.Data["repositories"]!.AsArray().Select(r => r!["repository"]!.GetValue<string>())];
    }

    private static string Name(string reference) => reference.Split('/')[2];

    private static async Task<string> BrowseAnonymousAsync(HttpClient client, string reference)
    {
        var response = await client.GetAsync("/api/browse/repos/" + reference);
        return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
    }

    private static async Task<IReadOnlyList<string>> BrowseAccountAnonymousAsync(HttpClient client, string login)
    {
        var rows = await client.GetFromJsonAsync<JsonArray>($"/api/browse/accounts/github/{login}/repos");
        return [.. rows!.Select(r => r!["name"]!.GetValue<string>())];
    }

    private Task<string> BrowseSignedInAsync(string userId, string reference)
    {
        var parts = reference.Split('/');
        return AsSignedInAsync(userId, async controller =>
        {
            var result = await controller.GetRepo(parts[0], parts[1], parts[2], CancellationToken.None);
            return Describe(result.Result ?? new OkObjectResult(result.Value));
        });
    }

    private Task<IReadOnlyList<string>> BrowseAccountSignedInAsync(string userId, string login)
        => AsSignedInAsync<IReadOnlyList<string>>(userId, async controller =>
        {
            var result = await controller.GetAccountRepos("github", login, CancellationToken.None);
            var rows = (IEnumerable<BrowseController.RepoInfo>)((OkObjectResult)result.Result!).Value!;
            return [.. rows.Select(r => r.Name)];
        });

    /// <summary>The controller inside the real host, under the cookie principal the website's sign-in gives this user.</summary>
    private async Task<T> AsSignedInAsync<T>(string userId, Func<BrowseController, Task<T>> call)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId)], "Identity.Application")),
        };
        accessor.HttpContext = context;
        try
        {
            var controller = ActivatorUtilities.CreateInstance<BrowseController>(scope.ServiceProvider);
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            return await call(controller);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private static string Describe(ActionResult result) => result switch
    {
        JsonResult json => $"{json.StatusCode} {JsonSerializer.Serialize(json.Value, JsonSerializerOptions.Web)}",
        ObjectResult obj => $"{obj.StatusCode ?? 200} {JsonSerializer.Serialize(obj.Value, JsonSerializerOptions.Web)}",
        StatusCodeResult status => $"{status.StatusCode}",
        _ => result.GetType().Name,
    };
}
