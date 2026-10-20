using System.Net;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Mcp;

/// <summary>
/// MCP PRD §6.2 as SeoTester's own tools meet it (plan M7a; the generic cases run on the library's fixtures in
/// <c>tests/MintPlayer.Spark.Tests/Mcp</c>): a grant of chosen types narrows the <c>seotester_*</c> tools, every read
/// answers another organization's scan or page like a missing one, anonymous agents cannot start a scan and get the
/// uniform 401 for pages too, and leaving the organization hides its sites from the agent's very next call.
/// </summary>
public partial class SeoToolsTests
{
    /// <summary>PageResult's id in the model (App_Data/Model/PageResult.json).</summary>
    private const string PageResultTypeId = "c7e5d8d7-f1c9-4dec-9492-ead5186dd59e";

    /// <summary>CheckDefinition's id in the model (App_Data/Model/CheckDefinition.json).</summary>
    private const string CheckDefinitionTypeId = "b474d13d-5d55-4abb-806f-bfd72d7382de";

    [Fact]
    public async Task A_grant_of_chosen_types_narrows_the_seotester_tools_and_a_scan_reads_its_checks_as_part_of_the_scan()
    {
        var site = await SeedSiteAsync(host, Acme);
        var run = await SeedRunAsync(site, DateTime.UtcNow.AddHours(-1), ("seo.title-present", "Fail", 1, [site.Origin + "/a"]));
        await SeedPageAsync(run, site.Origin + "/a", "A", new PageFinding { CheckId = "seo.title-present", Status = "Fail", Detail = "The page has no title." });
        var scansOnly = await TypedTokenAsync(Ann, SiteTypeId, AuditRunTypeId);
        var catalogueOnly = await TypedTokenAsync(Ann, CheckDefinitionTypeId);
        using var client = host.CreateClient();

        // The scan's checks are part of the scan: their titles and severities come along without CheckDefinition in the
        // grant. Reading the catalogue used to throw out of these tools (the SDK's generic error) for such a grant.
        var summary = await CallSettledAsync(client, "seotester_run_summary", new { site = site.Id }, scansOnly);
        var findings = await CallSettledAsync(client, "seotester_list_findings", new { site = site.Id }, scansOnly);
        var page = await SeoMcp.CallAsync(client, "seotester_get_page", new { site = site.Id, url = site.Origin + "/a" }, scansOnly);
        var explain = await SeoMcp.CallAsync(client, "seotester_explain_check", new { checkId = "seo.title-present" }, scansOnly);

        summary.IsToolError.Should().BeFalse(summary.Raw);
        summary.Data["checks"]![0]!["title"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace("the catalogue's title, read as part of the scan");
        findings.IsToolError.Should().BeFalse(findings.Raw);
        page.IsToolError.Should().BeFalse(page.Raw);
        explain.IsToolError.Should().BeTrue("the catalogue's own tool needs the catalogue in the grant");
        explain.ToolText.Should().Be(SparkMcpResult.NotFoundMessage);

        // A grant without the site types: no site tool reaches a site, generic or SeoTester's own.
        var sites = await CallSettledAsync(client, "seotester_list_sites", new { }, catalogueOnly);
        var scan = await SeoMcp.CallAsync(client, "seotester_run_summary", new { site = site.Id }, catalogueOnly);
        var generic = await SeoMcp.CallAsync(client, "seotester_get", new { type = "Site", id = site.Id }, catalogueOnly);
        var explained = await SeoMcp.CallAsync(client, "seotester_explain_check", new { checkId = "seo.title-present" }, catalogueOnly);

        sites.ToolText.Should().Be(SparkMcpResult.NotFoundMessage);
        scan.ToolText.Should().Be(SparkMcpResult.NotFoundMessage);
        generic.ToolText.Should().Be(SparkMcpResult.NotFoundMessage);
        explained.IsToolError.Should().BeFalse(explained.Raw);
    }

    [Fact]
    public async Task Every_seotester_read_answers_another_organizations_scan_or_page_like_a_missing_one()
    {
        var acme = await SeedSiteAsync(host, Acme);
        var globex = await SeedSiteAsync(host, Globex);
        await SeedRunAsync(acme, DateTime.UtcNow.AddHours(-2), ("seo.title-present", "Pass", 0, []));
        var globexRun = await SeedRunAsync(globex, DateTime.UtcNow.AddHours(-1), ("seo.title-present", "Fail", 1, [globex.Origin + "/a"]));
        var globexPage = await SeedPageAsync(globexRun, globex.Origin + "/a", "Globex", new PageFinding { CheckId = "seo.title-present", Status = "Fail", Detail = "missing" });
        var token = await SeoMcp.TokenAsync(host, Ann);
        using var client = host.CreateClient();

        var replies = new Dictionary<string, SeoMcp.Reply>
        {
            ["findings, another organization's site"] = await SeoMcp.CallAsync(client, "seotester_list_findings", new { site = globex.Id }, token),
            ["findings, no such site"] = await SeoMcp.CallAsync(client, "seotester_list_findings", new { site = "Sites/999999-Z" }, token),
            ["findings, another site's scan under Ann's site"] = await SeoMcp.CallAsync(client, "seotester_list_findings", new { site = acme.Id, run = globexRun.Id }, token),
            ["summary, another site's scan under Ann's site"] = await SeoMcp.CallAsync(client, "seotester_run_summary", new { site = acme.Id, run = globexRun.Id }, token),
            ["page, another organization's site"] = await SeoMcp.CallAsync(client, "seotester_get_page", new { site = globex.Id, url = globex.Origin + "/a" }, token),
            ["page, no such site"] = await SeoMcp.CallAsync(client, "seotester_get_page", new { site = "Sites/999999-Z", url = globex.Origin + "/a" }, token),
            ["status, another organization's scan"] = await SeoMcp.CallAsync(client, "seotester_scan_status", new { run = globexRun.Id }, token),
            ["status, no such scan"] = await SeoMcp.CallAsync(client, "seotester_scan_status", new { run = "AuditRuns/999999-Z" }, token),
        };

        foreach (var (what, reply) in replies)
        {
            reply.Status.Should().Be(HttpStatusCode.OK, what);
            reply.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, $"{what}: one answer for 'not yours' and 'not there' (D17)");
        }
        // Parity: the web refuses Ann the other organization's scan and page alike.
        (await LoadAsync(host, AuditRunTypeId, globexRun.Id!, Ann)).Status.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(host, PageResultTypeId, globexPage.Id!, Ann)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_agent_cannot_start_a_scan_and_gets_the_uniform_401_for_a_page()
    {
        var site = await SeedSiteAsync(host, Acme);
        var run = await SeedRunAsync(site, DateTime.UtcNow.AddHours(-1), ("seo.title-present", "Fail", 1, [site.Origin + "/a"]));
        await SeedPageAsync(run, site.Origin + "/a", "A", new PageFinding { CheckId = "seo.title-present", Status = "Fail", Detail = "missing" });
        using var client = host.CreateClient();

        var tools = await SeoMcp.ListToolsAsync(client, token: null);
        var start = await SeoMcp.CallAsync(client, "seotester_start_scan", new { site = site.Id }, token: null);
        var page = await SeoMcp.CallAsync(client, "seotester_get_page", new { site = site.Id, url = site.Origin + "/a" }, token: null);
        var unknown = await SeoMcp.CallAsync(client, "seotester_get_page", new { site = "Sites/999999-Z", url = site.Origin + "/a" }, token: null);

        tools.ToolNames.Should().NotContain("seotester_start_scan").And.Contain("seotester_explain_check");
        start.Status.Should().Be(HttpStatusCode.OK, start.Raw);
        start.ErrorMessage.Should().Be("Unknown tool: 'seotester_start_scan'", "anonymous calls are read-only (D17): the write tool is not there");
        foreach (var reply in new[] { page, unknown })
        {
            reply.Status.Should().Be(HttpStatusCode.Unauthorized, reply.Raw);
            reply.Response.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=");
            reply.Raw.Should().NotContain(site.Host);
        }
        page.ErrorMessage.Should().Be(unknown.ErrorMessage, "an existing and an unknown page answer alike");
        using var session = host.Database.OpenAsyncSession();
        (await session.Query<AuditRun>().Customize(c => c.WaitForNonStaleResults()).Where(r => r.SiteId == site.Id).CountAsync())
            .Should().Be(1, "no scan was started");
    }

    /// <summary>
    /// SeoTester's roles are organization memberships, read from the store per request (no cache in the Organizations
    /// package): leaving the organization hides its sites from the agent's very next call, with no new token (§6.2 "group
    /// removal denies the bearer's next call", S4). A member of its own, so the fixture's members stay as they are.
    /// </summary>
    [Fact]
    public async Task Leaving_the_organization_hides_its_sites_from_the_agents_next_call()
    {
        var user = $"SparkUsers/m7-{Guid.NewGuid():N}"[..28];
        var membership = OrganizationMembership.IdFor(user, Acme);
        using (var session = host.Database.OpenAsyncSession())
        {
            var name = user["SparkUsers/".Length..];
            await session.StoreAsync(new SparkUser
            {
                UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = name + "@example.com",
                NormalizedEmail = (name + "@example.com").ToUpperInvariant(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString("N"),
            }, user);
            await session.StoreAsync(new OrganizationMembership { OrganizationId = Acme, UserId = user, UserName = name, Role = OrganizationRole.Member, JoinedAtUtc = DateTime.UtcNow }, membership);
            await session.SaveChangesAsync();
        }
        var site = await SeedSiteAsync(host, Acme);
        await SeedRunAsync(site, DateTime.UtcNow.AddHours(-1), ("seo.title-present", "Pass", 0, []));
        var token = await SeoMcp.TokenAsync(host, user);
        using var client = host.CreateClient();

        var before = await CallSettledAsync(client, "seotester_run_summary", new { site = site.Id }, token);
        using (var session = host.Database.OpenAsyncSession())
        {
            session.Delete(membership);
            await session.SaveChangesAsync();
        }
        var after = await SeoMcp.CallAsync(client, "seotester_run_summary", new { site = site.Id }, token);

        before.IsToolError.Should().BeFalse(before.Raw);
        after.Status.Should().Be(HttpStatusCode.OK, "the token is still valid: leaving an organization changes no security stamp");
        after.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, "the membership is read on every call");
        (await LoadAsync(host, SiteTypeId, site.Id!, user)).Status.Should().Be(HttpStatusCode.NotFound, "the web agrees");
    }

    /// <summary>A personal access token of <paramref name="user"/> for the chosen <paramref name="typeIds"/> only, read only. A grant names types by id (D28).</summary>
    private async Task<string> TypedTokenAsync(string user, params string[] typeIds)
    {
        using var client = host.CreateClient();
        var (status, body, raw) = await SeoRequests.PostAsync(client, "/spark/auth/manage/agents/tokens", new
        {
            name = "types " + Guid.NewGuid().ToString("N")[..8],
            permissions = "Read",
            expiresInDays = 7,
            types = typeIds.Select(t => new { typeId = t, permissions = "Read" }).ToArray(),
        }, user);
        status.Should().Be(HttpStatusCode.OK, raw);
        return body.GetProperty("token").GetString()!;
    }
}
