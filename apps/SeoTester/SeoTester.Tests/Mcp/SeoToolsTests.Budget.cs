using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Mcp;

/// <summary>
/// Spike S9 (MCP PRD §6.1, D10; plan M7) on SeoTester: the largest realistic call of every tool answers in less than 10k
/// tokens with the tools' defaults (and list pages at their maximum). Tokens are estimated as characters / 4 (no Claude
/// tokenizer runs offline); the bound asserted is <see cref="SparkMcpResult.MaxCharacters"/> characters of the whole
/// answer text, note and fence included. The sizes are written to the test output for docs/spark_mcp_spikes.md (S9).
/// </summary>
/// <remarks>
/// An organization of its own, so the shared fixture's lists are untouched: 60 sites, and one verified site's scan with a
/// result for every rule of the shipped catalogue (60% failing or warning, 5 sample URLs of 120–300 characters each) and
/// 300 pages of 25 failing or warning findings each, with 3–5 selectors of 40–150 characters and the page texts at their
/// stored maximum.
/// </remarks>
public partial class SeoToolsTests
{
    [Fact]
    public async Task Budget_the_largest_realistic_call_of_every_seotester_tool_fits_the_output_budget()
    {
        var (user, site, run, widestPage) = await SeedProductionSizeAsync();
        var token = await SeoMcp.TokenAsync(host, user);
        using var client = host.CreateClient();
        var largestRule = (await RulesAsync()).OrderByDescending(r => (r.HowToFixEn?.Length ?? 0) + (r.SnippetNginx?.Length ?? 0) + (r.SnippetApache?.Length ?? 0) + (r.SnippetIis?.Length ?? 0) + (r.SnippetHtml?.Length ?? 0)).First();

        var calls = new (string Label, string Tool, object Arguments)[]
        {
            ("list_sites (defaults: 50)", "seotester_list_sites", new { }),
            ("list_sites (take 100, the maximum)", "seotester_list_sites", new { take = 100 }),
            ("run_summary (every rule of the catalogue)", "seotester_run_summary", new { site = site.Id }),
            ("list_findings (defaults: 100 pages)", "seotester_list_findings", new { site = site.Id }),
            ("list_findings (take 200, the maximum)", "seotester_list_findings", new { site = site.Id, take = 200 }),
            ("get_page (25 findings, texts at their maximum)", "seotester_get_page", new { site = site.Id, url = widestPage }),
            ("scan_status", "seotester_scan_status", new { run = run.Id }),
            ($"explain_check ({largestRule.CheckId}, the longest)", "seotester_explain_check", new { checkId = largestRule.CheckId }),
        };

        var failures = new List<string>();
        foreach (var (label, tool, arguments) in calls)
        {
            var reply = await CallSettledAsync(client, tool, arguments, token);
            var text = reply.ToolText;
            output.WriteLine($"S9 SeoTester | {label} | {text.Length} chars | ~{(text.Length + 3) / 4} tokens{(reply.IsToolError ? " | ERROR " + text : "")}");
            if (reply.IsToolError || text.Length == 0)
                failures.Add($"{label}: {text}{reply.ErrorMessage}");
            else if (text.Length >= SparkMcpResult.MaxCharacters || text.Contains("(truncated", StringComparison.Ordinal))
                failures.Add($"{label}: {text.Length} characters");
        }

        failures.Should().BeEmpty("every default page stays under 10k tokens (S9)");
    }

    private async Task<IReadOnlyList<CheckDefinition>> RulesAsync()
    {
        using var session = host.Database.OpenAsyncSession();
        return await session.Query<CheckDefinition>().Customize(c => c.WaitForNonStaleResults()).Take(1024).ToListAsync();
    }

    private async Task<(string User, Site Site, AuditRun Run, string WidestPage)> SeedProductionSizeAsync()
    {
        var random = new Random(9);
        string Text(int length) => new(Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26))).ToArray());
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = $"SparkUsers/m7-budget-{suffix}";
        var organization = $"Organizations/m7-budget-{suffix}";
        var rules = await RulesAsync();
        rules.Count.Should().BeGreaterThan(50, "the shipped catalogue is seeded at startup");

        using (var session = host.Database.OpenAsyncSession())
        {
            var name = user["SparkUsers/".Length..];
            await session.StoreAsync(new SparkUser
            {
                UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = name + "@example.com",
                NormalizedEmail = (name + "@example.com").ToUpperInvariant(), EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString("N"),
            }, user);
            await session.StoreAsync(new Organization { Name = "Budget " + suffix, IsPersonal = false, CreatedBy = user, CreatedAtUtc = DateTime.UtcNow }, organization);
            await session.StoreAsync(new OrganizationMembership { OrganizationId = organization, UserId = user, UserName = name, Role = OrganizationRole.Owner, JoinedAtUtc = DateTime.UtcNow },
                OrganizationMembership.IdFor(user, organization));
            await session.SaveChangesAsync();
        }

        Site? site = null;
        for (var i = 0; i < 60; i++)
        {
            var seeded = await SeoApi.SeedSiteAsync(host, organization, verified: i == 0, hostName: $"budget-{i:D2}-{suffix}-{Text(30)}.example.com");
            site ??= seeded;
        }

        string Url() => $"{site!.Origin}/{Text(20)}/{Text(30)}?{Text(10)}={Text(20 + random.Next(150))}";
        var failing = rules.Where((_, i) => i % 5 < 3).ToList();
        var run = await SeedRunAsync(site!, DateTime.UtcNow.AddHours(-1), [.. rules.Select((r, i) => (
            r.CheckId!,
            i % 5 < 3 ? (i % 2 == 0 ? "Fail" : "Warn") : "Pass",
            i % 5 < 3 ? 300 : 0,
            i % 5 < 3 ? Enumerable.Range(0, 5).Select(_ => Url()).ToArray() : Array.Empty<string>()))]);

        string? widest = null;
        using (var bulk = host.Database.BulkInsert())
        {
            for (var p = 0; p < 300; p++)
            {
                var url = $"{site!.Origin}/page/{p:D3}/{Text(40)}";
                widest ??= url;
                await bulk.StoreAsync(new PageResult
                {
                    Id = PageResult.IdFor(site.Id!, url),
                    RunId = run.Id,
                    OrganizationId = organization,
                    Url = url,
                    StatusCode = 200,
                    Depth = 1 + p % 5,
                    LoadMs = 900 + p,
                    Title = Text(300),
                    Description = Text(500),
                    H1 = Text(300),
                    OgTitle = Text(300),
                    OgDescription = Text(500),
                    OgImage = $"https://cdn.example.com/{Text(200)}.png",
                    OgUrl = url,
                    Findings = [.. failing.Take(25).Select((r, f) => new PageFinding
                    {
                        CheckId = r.CheckId!,
                        Status = f % 2 == 0 ? "Fail" : "Warn",
                        Detail = Text(150),
                        Selectors = [.. Enumerable.Range(0, 3 + f % 3).Select(s => $"body > main > div.{Text(10)}:nth-child({s + 1}) > {Text(20 + random.Next(110))}")],
                    })],
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                });
            }
        }
        host.WaitForIndexes();
        return (user, site!, run, widest!);
    }
}
