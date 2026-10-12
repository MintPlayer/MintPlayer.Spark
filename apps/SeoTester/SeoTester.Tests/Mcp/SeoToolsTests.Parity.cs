using System.Net;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Testing;
using SeoTester.Entities;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Mcp;

/// <summary>
/// Spike S5 (MCP PRD §6.1, D9; plan M7) on SeoTester: Ann (Acme's owner), Vic (Acme's viewer), Bob (Globex's owner) and
/// an anonymous caller ask each SeoTester tool, and the generic tools, what the web's own endpoints answer them: the sites
/// (<c>queries/execute</c> on Sites), a scan and its checks (<c>po/load</c> on AuditRun), a page and its findings
/// (<c>po/load</c> on PageResult, the scan's pages through <c>run-pages</c>), a rule (<c>po/load</c> on CheckDefinition).
/// Each comparison also states what that caller should see, so a check removed from a shared service fails it too.
/// </summary>
/// <remarks>
/// The fixture database is shared with the collection's other tests, so sites are compared by presence (the ones seeded
/// here) and totals at the same moment, not as whole lists. Differences by design: <c>PageResult</c> is not exposed to
/// agents (reached through the scan tools only), and an anonymous refusal is HTTP 401 on both surfaces.
/// </remarks>
public partial class SeoToolsTests
{
    private sealed record ParityCaller(string Name, string? User, string? Token);

    [Fact]
    public async Task Parity_sites_scans_pages_and_rules_answer_what_the_web_shows_each_user()
    {
        var acme = await SeedSiteAsync(host, Acme);
        var globex = await SeedSiteAsync(host, Globex);
        var run = await SeedRunAsync(acme, DateTime.UtcNow.AddHours(-1),
            ("seo.title-present", "Fail", 2, [acme.Origin + "/a", acme.Origin + "/b"]),
            ("security.hsts", "Pass", 0, []));
        await SeedPageAsync(run, acme.Origin + "/a", "Page A", new PageFinding { CheckId = "seo.title-present", Status = "Fail", Detail = "The page has no title.", Selectors = ["head > title"] });
        await SeedPageAsync(run, acme.Origin + "/b", "Page B", new PageFinding { CheckId = "seo.title-present", Status = "Fail", Detail = "The page has no title." });
        var globexRun = await SeedRunAsync(globex, DateTime.UtcNow.AddHours(-1), ("seo.title-present", "Pass", 0, []));
        var callers = new[]
        {
            new ParityCaller("ann", Ann, await SeoMcp.TokenAsync(host, Ann)),
            new ParityCaller("vic", Vic, await SeoMcp.TokenAsync(host, Vic)),
            new ParityCaller("bob", Bob, await SeoMcp.TokenAsync(host, Bob)),
            new ParityCaller("anonymous", null, null),
        };
        using var client = host.CreateClient();
        await SeoMcp.CallAsync(client, "seotester_list_sites", new { take = 100 }, callers[0].Token);
        await WebAsync("/spark/queries/execute", Wire.Query(SitesQueryId, new { take = 1000 }), Ann);
        await WebAsync("/spark/queries/execute", Wire.Query("run-pages", new { parentId = run.Id, parentType = "AuditRun", take = 100 }), Ann);
        host.WaitForIndexes();
        var differences = new List<string>();

        foreach (var caller in callers)
        {
            var seesAcme = caller.Name is "ann" or "vic";
            var seesGlobex = caller.Name == "bob";

            // list_sites ↔ the Sites query.
            var sites = await SeoMcp.CallAsync(client, "seotester_list_sites", new { take = 100 }, caller.Token);
            var (sitesStatus, webSites) = await WebAsync("/spark/queries/execute", Wire.Query(SitesQueryId, new { take = 1000 }), caller.User);
            if (caller.Token is null)
            {
                Same(differences, $"{caller.Name} list_sites status", sites.Status, sitesStatus);
            }
            else
            {
                var agentIds = sites.Data["sites"]!.AsArray().Select(s => s!["site"]!.GetValue<string>()).ToHashSet();
                var webIds = webSites!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToHashSet();
                Same(differences, $"{caller.Name} list_sites total", sites.Data["totalSites"]!.GetValue<int>(), webSites["totalItems"]!.GetValue<int>());
                foreach (var site in new[] { acme, globex })
                    Same(differences, $"{caller.Name} list_sites has {site.Host}", agentIds.Contains(site.Id!), webIds.Contains(site.Id!));
                agentIds.Contains(acme.Id!).Should().Be(seesAcme, caller.Name);
                agentIds.Contains(globex.Id!).Should().Be(seesGlobex, caller.Name);
            }

            // run_summary and scan_status ↔ po/load AuditRun.
            var summary = await SeoMcp.CallAsync(client, "seotester_run_summary", new { site = acme.Id }, caller.Token);
            var status = await SeoMcp.CallAsync(client, "seotester_scan_status", new { run = run.Id }, caller.Token);
            var (runStatus, webRun) = await WebAsync("/spark/po/load", Wire.Typed(AuditRunTypeId, id: run.Id), caller.User);
            if (runStatus != HttpStatusCode.OK)
            {
                Refused(differences, $"{caller.Name} run_summary", summary, runStatus);
                Refused(differences, $"{caller.Name} scan_status", status, runStatus);
            }
            else
            {
                summary.IsToolError.Should().BeFalse(summary.Raw);
                Same(differences, $"{caller.Name} run_summary scan", summary.Data["scan"]!["id"]!.GetValue<string>(), webRun!["id"]!.GetValue<string>());
                Same(differences, $"{caller.Name} run_summary score", summary.Data["scan"]!["score"]!.ToJsonString(), Value(webRun, "Score")?.ToJsonString());
                var webChecks = Rows(webRun, "CheckResults").ToDictionary(r => Value(r, "CheckId")!.GetValue<string>());
                var agentChecks = summary.Data["checks"]!.AsArray().ToDictionary(c => c!["check"]!.GetValue<string>(), c => c!);
                Same(differences, $"{caller.Name} run_summary checks", string.Join(',', agentChecks.Keys.Order()), string.Join(',', webChecks.Keys.Order()));
                foreach (var (check, agentCheck) in agentChecks)
                {
                    if (!webChecks.TryGetValue(check, out var webCheck))
                        continue;
                    Same(differences, $"{caller.Name} {check} status", agentCheck["status"]!.GetValue<string>(), Value(webCheck, "Status")!.GetValue<string>());
                    Same(differences, $"{caller.Name} {check} affected", agentCheck["affected"]!.GetValue<int>(), Value(webCheck, "Affected")!.GetValue<int>());
                    Same(differences, $"{caller.Name} {check} samples", agentCheck["sampleUrls"]?.ToJsonString() ?? "[]", Value(webCheck, "SampleUrls")?.ToJsonString() ?? "[]");
                }
                status.IsToolError.Should().BeFalse(status.Raw);
                Same(differences, $"{caller.Name} scan_status status", status.Data["status"]!.GetValue<string>(), Value(webRun, "Status")!.GetValue<string>());
            }
            (runStatus == HttpStatusCode.OK).Should().Be(seesAcme, $"{caller.Name} reads Acme's scan on the web");

            // get_page ↔ po/load PageResult; list_findings ↔ run-pages + po/load of each page.
            var page = await SeoMcp.CallAsync(client, "seotester_get_page", new { site = acme.Id, url = acme.Origin + "/a" }, caller.Token);
            var (pageStatus, webPage) = await WebAsync("/spark/po/load", Wire.Typed(PageResultTypeId, id: PageResult.IdFor(acme.Id!, acme.Origin + "/a")), caller.User);
            var findings = await SeoMcp.CallAsync(client, "seotester_list_findings", new { site = acme.Id }, caller.Token);
            var (pagesStatus, webPages) = await WebAsync("/spark/queries/execute", Wire.Query("run-pages", new { parentId = run.Id, parentType = "AuditRun", take = 100 }), caller.User);
            if (pageStatus != HttpStatusCode.OK)
            {
                Refused(differences, $"{caller.Name} get_page", page, pageStatus);
                Refused(differences, $"{caller.Name} list_findings", findings, pagesStatus);
            }
            else
            {
                page.IsToolError.Should().BeFalse(page.Raw);
                Same(differences, $"{caller.Name} get_page title", page.Data["page"]!["title"]?.GetValue<string>(), Value(webPage!, "Title")?.GetValue<string>());
                Same(differences, $"{caller.Name} get_page findings",
                    string.Join(',', page.Data["page"]!["findings"]!.AsArray().Select(f => $"{f!["check"]}:{f["status"]}").Order()),
                    string.Join(',', Rows(webPage!, "Findings").Select(f => $"{Value(f, "CheckId")}:{Value(f, "Status")}").Order()));

                findings.IsToolError.Should().BeFalse(findings.Raw);
                var agentUrls = findings.Data["rules"]!.AsArray().SelectMany(r => r!["pages"]!.AsArray().Select(p => p!["url"]!.GetValue<string>())).Distinct().Order();
                var webUrls = webPages!["items"]!.AsArray().Select(i => i!["values"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "Url")!["value"]!.GetValue<string>()).Order();
                Same(differences, $"{caller.Name} list_findings pages", string.Join(',', agentUrls), string.Join(',', webUrls));
            }
            (pageStatus == HttpStatusCode.OK).Should().Be(seesAcme, $"{caller.Name} reads Acme's page on the web");

            // The generic get ↔ po/load: a site, and the other organization's site.
            foreach (var site in new[] { acme, globex })
            {
                var agentSite = await SeoMcp.CallAsync(client, "seotester_get", new { type = "Site", id = site.Id }, caller.Token);
                var (siteStatus, webSite) = await WebAsync("/spark/po/load", Wire.Typed(SiteTypeId, id: site.Id), caller.User);
                if (siteStatus != HttpStatusCode.OK)
                {
                    Refused(differences, $"{caller.Name} get Site {site.Host}", agentSite, siteStatus);
                    continue;
                }
                agentSite.IsToolError.Should().BeFalse(agentSite.Raw);
                var agentAttributes = agentSite.Data["attributes"]!.AsObject();
                var webAttributes = webSite!["attributes"]!.AsArray();
                // By design (D6, D19): OrganizationId references Organization, a type agents are not given, so it is hidden
                // from the agent like any reference to a type the caller may not read; everything else is the web's.
                agentAttributes.Select(a => a.Key).Should().NotContain("OrganizationId");
                Same(differences, $"{caller.Name} get Site attributes",
                    string.Join(',', agentAttributes.Select(a => a.Key).Order()),
                    string.Join(',', webAttributes.Select(a => a!["name"]!.GetValue<string>()).Where(n => n != "OrganizationId").Order()));
                Same(differences, $"{caller.Name} get Site origin", agentAttributes["Origin"]?.GetValue<string>(), Value(webSite, "Origin")?.GetValue<string>());
            }

            // explain_check ↔ po/load CheckDefinition (anonymous visitors read the catalogue: D24 §4.7).
            var explain = await SeoMcp.CallAsync(client, "seotester_explain_check", new { checkId = "seo.title-present" }, caller.Token);
            var (ruleStatus, webRule) = await WebAsync("/spark/po/load", Wire.Typed(CheckDefinitionTypeId, id: CheckDefinition.IdFor("seo.title-present")), caller.User);
            ruleStatus.Should().Be(HttpStatusCode.OK, caller.Name);
            explain.IsToolError.Should().BeFalse(explain.Raw);
            Same(differences, $"{caller.Name} explain_check title", explain.Data["title"]?.GetValue<string>(), Value(webRule!, "TitleEn")?.GetValue<string>());
            Same(differences, $"{caller.Name} explain_check severity", explain.Data["severity"]?.GetValue<string>(), Value(webRule!, "Severity")?.GetValue<string>());
        }

        differences.Should().BeEmpty();
        _ = globexRun;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> WebAsync(string url, object payload, string? user)
    {
        var (status, _, raw) = await PostAsync(host, url, payload, user);
        return (status, string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw));
    }

    /// <summary>A persistent object's attribute value, by name.</summary>
    private static JsonNode? Value(JsonNode po, string name)
        => po["attributes"]!.AsArray().SingleOrDefault(a => a!["name"]!.GetValue<string>() == name)?["value"];

    /// <summary>A persistent object's detail rows (AsDetail array: the web carries them in <c>objects</c>).</summary>
    private static IEnumerable<JsonNode> Rows(JsonNode po, string name)
        => po["attributes"]!.AsArray().SingleOrDefault(a => a!["name"]!.GetValue<string>() == name)?["objects"]?.AsArray().OfType<JsonNode>() ?? [];

    /// <summary>A refusal on the web must be the agent's uniform refusal: HTTP 401 for both when anonymous, else "Not found." for a 404.</summary>
    private static void Refused(List<string> differences, string what, SeoMcp.Reply agent, HttpStatusCode web)
    {
        var expected = web == HttpStatusCode.Unauthorized
            ? agent.Status == HttpStatusCode.Unauthorized
            : web == HttpStatusCode.NotFound && agent.ToolText == SparkMcpResult.NotFoundMessage;
        if (!expected)
            differences.Add($"{what}: the web answered {(int)web}, the agent {(int)agent.Status} '{agent.ToolText}{agent.ErrorMessage}'");
    }

    private static void Same<T>(List<string> differences, string what, T agent, T web)
    {
        if (!EqualityComparer<T>.Default.Equals(agent, web))
            differences.Add($"{what}: agent {agent} web {web}");
    }
}
