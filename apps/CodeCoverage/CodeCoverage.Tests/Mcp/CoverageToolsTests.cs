using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeCoverage.Tests.Mcp._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using MintPlayer.Spark.Mcp;
using Xunit;

namespace CodeCoverage.Tests.Mcp;

/// <summary>
/// What CodeCoverage's MCP tools answer (MCP PRD §4.8, plan M6), through the real <c>/mcp</c> endpoint: the hotspot order
/// spike S8 settled on, the line ranges, patch coverage, drops, the output cap, and the one prompt.
/// </summary>
[Collection(CoverageMcpHostCollection.Name)]
public partial class CoverageToolsTests(CoverageMcpHostFixture fixture)
{
    [Fact]
    public async Task Hotspots_rank_by_uncovered_lines_and_give_covered_percent_and_ranges_as_columns()
    {
        using var client = Client();

        var data = (await CallAsync(client, "codecoverage_hotspots", new { repository = "github/acme/open", take = 10 })).Data;

        data["commit"]!.GetValue<string>().Should().Be(CoverageMcpSeed.OpenHead);
        data["totalFiles"]!.GetValue<int>().Should().Be(5, "a fully covered file is no hotspot");
        var files = data["files"]!.AsArray().Select(f => f!).ToList();
        files.Select(f => f["path"]!.GetValue<string>()).Should().Equal(
            "src/big-untested.ts", "src/half.ts", "src/one-gap.ts", "src/a-tie.ts", "src/b-tie.ts");
        files.Select(f => f["uncoveredLines"]!.GetValue<int>()).Should().Equal(200, 150, 100, 40, 40);
        files.Select(f => f["coveredPercent"]!.GetValue<double>()).Should().Equal(0, 50, 16.7, 20, 11.1);
        files.Select(f => f["uncoveredRanges"]!.GetValue<int>()).Should().Equal(1, 30, 1, 2, 1);

        // The fixture discriminates: the ease score PRD §4.8.1 used to propose (S8's H1: uncovered × max(covered
        // fraction, 0.05) / ranges) orders these files differently, so the order above is the uncovered-lines rule.
        var ease = CoverageMcpSeed.OpenHeadFiles
            .Where(f => f.Coverable > f.Covered)
            .OrderByDescending(f => (f.Coverable - f.Covered) * Math.Max((double)f.Covered / f.Coverable, 0.05) / f.Ranges)
            .Select(f => f.Path);
        ease.Should().NotEqual(files.Select(f => f["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Hotspots_page_and_narrow_to_a_path()
    {
        using var client = Client();

        var page = (await CallAsync(client, "codecoverage_hotspots", new { repository = "github/acme/open", skip = 1, take = 2 })).Data;
        page["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Should().Equal("src/half.ts", "src/one-gap.ts");
        page["nextSkip"]!.GetValue<int>().Should().Be(3);

        var narrowed = (await CallAsync(client, "codecoverage_hotspots", new { repository = "github/acme/open", pathPrefix = "./lib/" })).Data;
        narrowed["totalFiles"]!.GetValue<int>().Should().Be(0, "lib/ holds only the fully covered file");
    }

    [Fact]
    public async Task File_gives_uncovered_ranges_and_partly_covered_branches_by_line_number_only()
    {
        using var client = Client();

        var head = (await CallAsync(client, "codecoverage_file", new { repository = "github/acme/open", path = "src/half.ts" })).Data;
        head["commit"]!.GetValue<string>().Should().Be(CoverageMcpSeed.OpenHead);
        var file = head["file"]!;
        file["uncoveredLines"]!.GetValue<int>().Should().Be(150);
        file["uncoveredRanges"]!.AsArray().Count.Should().Be(30);
        file["uncoveredRanges"]![0]!.GetValue<string>().Should().Be("1-5", "30 runs of 5, each followed by one covered line");
        file["uncoveredRanges"]![1]!.GetValue<string>().Should().Be("7-11");
        file["partialBranches"]![0]!["line"]!.GetValue<int>().Should().Be(1);
        file["partialBranches"]![0]!["total"]!.GetValue<int>().Should().Be(2);
        file.AsObject().ContainsKey("source").Should().BeFalse("the tool never sends source");

        var parent = (await CallAsync(client, "codecoverage_file", new { repository = "github/acme/open", path = "src/half.ts", commit = CoverageMcpSeed.OpenParent })).Data;
        parent["file"]!["uncoveredLines"]!.GetValue<int>().Should().Be(130, "a named commit reads that commit");

        var unknown = (await CallAsync(client, "codecoverage_file", new { repository = "github/acme/open", path = "src/nope.ts" })).Data;
        unknown["note"]!.GetValue<string>().Should().Contain("no file at that path");

        var malformed = await McpWire.CallAsync(client, "codecoverage_file", new { repository = "github/acme/open", path = "src/half.ts", commit = "abc" });
        malformed.IsToolError.Should().BeTrue();
        malformed.ToolText.Should().Contain("40-character sha", "a malformed argument is said, since it depends on the arguments only");
    }

    [Fact]
    public async Task Patch_gives_the_patch_number_and_the_uncovered_added_lines()
    {
        using var client = Client();

        var data = (await CallAsync(client, "codecoverage_patch", new { repository = "github/acme/open", pullRequest = 7 })).Data;

        data["commit"]!.GetValue<string>().Should().Be(CoverageMcpSeed.OpenPullRequestHead);
        data["linesCovered"]!.GetValue<int>().Should().Be(3);
        data["linesCoverable"]!.GetValue<int>().Should().Be(5);
        data["patchPercent"]!.GetValue<double>().Should().Be(60);
        var files = data["uncoveredFiles"]!.AsArray();
        files.Count.Should().Be(1, "README.md is in the diff but was not measured: skipped, not zeroed");
        files[0]!["path"]!.GetValue<string>().Should().Be("src/feature.ts");
        files[0]!["ranges"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("4-5");

        var none = (await CallAsync(client, "codecoverage_patch", new { repository = "github/acme/open", pullRequest = 99 })).Data;
        none["note"]!.GetValue<string>().Should().Contain("No coverage");
    }

    [Fact]
    public async Task Drops_name_the_commit_and_the_files_whose_uncovered_lines_grew()
    {
        using var client = Client();

        var drops = (await CallAsync(client, "codecoverage_drops", new { repository = "github/acme/open" })).Data["drops"]!.AsArray();

        drops.Count.Should().Be(1);
        var drop = drops[0]!;
        drop["sha"]!.GetValue<string>().Should().Be(CoverageMcpSeed.OpenHead);
        drop["deltaVsParent"]!.GetValue<double>().Should().Be(-2.5);
        drop["message"]!.GetValue<string>().Should().Be("Add the exporter", "the first line only");
        drop["files"]!.AsArray().Select(f => (f!["path"]!.GetValue<string>(), f["uncoveredBefore"]!.GetValue<int>(), f["uncoveredAfter"]!.GetValue<int>()))
            .Should().Equal(("src/big-untested.ts", 0, 200), ("src/half.ts", 130, 150));

        (await CallAsync(client, "codecoverage_drops", new { repository = "github/acme/open", since = "2026-10-01" })).Data["drops"]!.AsArray()
            .Count.Should().Be(0, "the drop is from September");
    }

    [Fact]
    public async Task Summary_gives_totals_trend_drops_and_branches()
    {
        using var client = Client();

        var data = (await CallAsync(client, "codecoverage_summary", new { repository = "github/acme/open" })).Data;

        data["branch"]!.GetValue<string>().Should().Be("main");
        data["trend"]!.AsArray().Select(p => p!["sha"]!.GetValue<string>()).Should().Equal(CoverageMcpSeed.OpenParent, CoverageMcpSeed.OpenHead);
        data["largestDrops"]!.AsArray().Select(d => d!["sha"]!.GetValue<string>()).Should().Equal(CoverageMcpSeed.OpenHead);
        data["branches"]!.AsArray().Select(b => b!.GetValue<string>()).Should().Equal("main", "feature/export");
    }

    /// <summary>
    /// D10: every answer stays below the output cap whatever the repository holds, and stays JSON, because each tool bounds
    /// its own lists instead of relying on the cap's cut (which would end mid-document).
    /// </summary>
    [Fact]
    public async Task The_largest_answers_fit_the_output_cap_without_being_cut()
    {
        using var client = Client();

        var calls = new (string Tool, object Arguments)[]
        {
            ("codecoverage_hotspots", new { repository = "github/acme/sprawl", take = 50 }),
            ("codecoverage_file", new { repository = "github/acme/sprawl", path = "src/zigzag.ts" }),
            ("codecoverage_summary", new { repository = "github/acme/sprawl" }),
            ("codecoverage_list_repositories", new { take = 50 }),
            ("codecoverage_drops", new { repository = "github/acme/open", take = 10 }),
        };

        foreach (var (tool, arguments) in calls)
        {
            var reply = await McpWire.CallAsync(client, tool, arguments);
            reply.IsToolError.Should().BeFalse($"{tool}: {reply.ToolText}");
            reply.ToolText.Length.Should().BeLessThan(SparkMcpResult.MaxCharacters, tool);
            reply.ToolText.Should().NotContain("(truncated", tool);
            _ = reply.Data; // parses: still one JSON document
        }

        var hotspots = (await CallAsync(client, "codecoverage_hotspots", new { repository = "github/acme/sprawl", take = 50 })).Data;
        hotspots["files"]!.AsArray().Count.Should().Be(50);
        hotspots["nextSkip"]!.GetValue<int>().Should().Be(50, "the rest is a page away");

        var zigzag = (await CallAsync(client, "codecoverage_file", new { repository = "github/acme/sprawl", path = "src/zigzag.ts" })).Data["file"]!;
        zigzag["uncoveredRanges"]!.AsArray().Count.Should().Be(400);
        zigzag["omittedRanges"]!.GetValue<int>().Should().Be(2600, "3000 ranges, the first 400 shown and the rest counted");
    }

    [Fact]
    public async Task Repository_strings_are_fenced_as_untrusted_data()
    {
        using var client = Client();

        var reply = await McpWire.CallAsync(client, "codecoverage_summary", new { repository = "github/acme/open" });

        reply.ToolText.Should().StartWith("Text between <data-", "branch names, paths and commit messages come from whoever pushes to the repository");
        FenceTag().Matches(reply.ToolText).Select(m => m.Value).Distinct().Should().ContainSingle("one random tag per call");
    }

    [Fact]
    public async Task The_prompt_names_only_tools_the_server_lists()
    {
        using var client = Client();

        var tools = (await McpWire.SendAsync(client, "tools/list", new { })).Result!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();
        foreach (var name in new[] { "codecoverage_list_repositories", "codecoverage_summary", "codecoverage_hotspots",
            "codecoverage_file", "codecoverage_patch", "codecoverage_drops", "codecoverage_query" })
        {
            tools.Should().Contain(name);
        }

        var prompts = (await McpWire.SendAsync(client, "prompts/list", new { })).Result!["prompts"]!.AsArray();
        prompts.Select(p => p!["name"]!.GetValue<string>()).Should().Equal("codecoverage_raise_coverage");

        var prompt = await McpWire.SendAsync(client, "prompts/get",
            new { name = "codecoverage_raise_coverage", arguments = new { repository = "github/acme/open" } });
        var text = prompt.Result!["messages"]![0]!["content"]!["text"]!.GetValue<string>();
        text.Should().Contain("github/acme/open").And.Contain("its default branch");
        ToolName().Matches(text).Select(m => m.Value).Should().OnlyContain(name => tools.Contains(name));
    }

    private HttpClient Client() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A call that must succeed, anonymously: every repository used here is public.</summary>
    private static async Task<McpWire.Reply> CallAsync(HttpClient client, string tool, object arguments)
    {
        var reply = await McpWire.CallAsync(client, tool, arguments);
        reply.IsToolError.Should().BeFalse($"{tool}: {reply.ToolText}");
        return reply;
    }

    [GeneratedRegex("<data-[0-9a-f]+>")]
    private static partial Regex FenceTag();

    [GeneratedRegex("codecoverage_[a-z_]+")]
    private static partial Regex ToolName();
}
