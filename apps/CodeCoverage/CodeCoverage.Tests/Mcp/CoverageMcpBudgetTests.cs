using System.Text.Json.Nodes;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using CodeCoverage.Tests.Mcp._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Mcp;
using Raven.Client.Documents;
using Xunit;
using Xunit.Abstractions;

namespace CodeCoverage.Tests.Mcp;

/// <summary>
/// Spike S9 (MCP PRD §6.1, D10; plan M7) on CodeCoverage: the largest realistic call of every tool, on a production-sized
/// coverage database, answers in less than 10k tokens with the tools' defaults (and at their maximum page sizes). Tokens
/// are estimated as characters / 4 (no Claude tokenizer runs offline); the bound asserted is
/// <see cref="SparkMcpResult.MaxCharacters"/> characters of the whole answer text, fence and note included. The sizes are
/// written to the test output for docs/spark_mcp_spikes.md (S9).
/// </summary>
/// <remarks>
/// The shape follows production (docs/code-coverage/multi-forge-plan.md: 179 repositories; the largest report 31,942
/// coverable lines in 736 files), with every list past its cap: a 2,400-line file of alternating coverage (1,200 ranges,
/// 300 partly covered lines), 40 commits on the default branch with 300-character messages and drops every third one, 25
/// covered branches with long names, and a pull request whose diff touches 45 files with 60 uncovered hunks each.
/// </remarks>
[Collection(CoverageMcpBudgetCollection.Name)]
public class CoverageMcpBudgetTests(CoverageMcpBudgetFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task The_largest_realistic_call_of_every_tool_fits_the_output_budget()
    {
        const string repository = "github/" + CoverageMcpBudgetFixture.Owner + "/" + CoverageMcpBudgetFixture.Monorepo;
        var calls = new (string Label, string Tool, object Arguments)[]
        {
            ("list_repositories (defaults: 25)", "codecoverage_list_repositories", new { }),
            ("list_repositories (take 50, the maximum)", "codecoverage_list_repositories", new { take = 50 }),
            ("summary (defaults)", "codecoverage_summary", new { repository }),
            ("hotspots (defaults: 10)", "codecoverage_hotspots", new { repository }),
            ("hotspots (take 50, the maximum)", "codecoverage_hotspots", new { repository, take = 50 }),
            ("file, 1,200 ranges and 300 partial lines", "codecoverage_file", new { repository, path = CoverageMcpBudgetFixture.BigFile }),
            ("patch, 45 files x 60 uncovered hunks", "codecoverage_patch", new { repository, pullRequest = CoverageMcpBudgetFixture.PullRequest }),
            ("drops (defaults: 5)", "codecoverage_drops", new { repository }),
            ("drops (take 10, the maximum)", "codecoverage_drops", new { repository, take = 10 }),
        };

        var failures = new List<string>();
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var (label, tool, arguments) in calls)
        {
            var reply = await McpWire.CallAsync(client, tool, arguments);
            var text = reply.ToolText;
            output.WriteLine($"S9 CodeCoverage | {label} | {text.Length} chars | ~{(text.Length + 3) / 4} tokens{(reply.IsToolError ? " | ERROR " + text : "")}");
            if (reply.IsToolError || text.Length == 0)
                failures.Add($"{label}: {text}{reply.ErrorMessage}");
            else if (text.Length >= SparkMcpResult.MaxCharacters || text.Contains("(truncated", StringComparison.Ordinal))
                failures.Add($"{label}: {text.Length} characters");
        }

        failures.Should().BeEmpty("every default page stays under 10k tokens (S9)");
    }
}

/// <summary>Non-parallel, like the other host collections: Spark's registry is process-wide (see <see cref="CoverageMcpHostCollection"/>).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoverageMcpBudgetCollection : ICollectionFixture<CoverageMcpBudgetFixture>
{
    public const string Name = "Coverage MCP budget host";
}

/// <summary>
/// The real application over a production-sized database of its own (the MCP host's repositories stay exactly what its
/// tests expect), read anonymously: every repository here is public. GitHub answers from <see cref="StubGitHubApi"/>.
/// </summary>
public sealed class CoverageMcpBudgetFixture : CoverageRavenTest, IAsyncLifetime
{
    public const string Owner = "budget-org";
    public const string Monorepo = "monorepo";
    public const long MonorepoId = 9301;
    public const int PullRequest = 42;
    public const string BigFile = "libs/spark/MintPlayer.Spark/Services/QueryExecutorWithAVeryLongNameForTheBudget.cs";

    public IDocumentStore Store { get; private set; } = null!;
    public CoverageWebAppFactory Factory { get; private set; } = null!;
    internal StubGitHubApi.Script GitHubApi { get; } = new();

    public async Task InitializeAsync()
    {
        Store = GetDocumentStore();
        Factory = new CoverageWebAppFactory(Store, services =>
            {
                services.RemoveAll<IGitHubUserTokenService>();
                services.AddSingleton<IGitHubUserTokenService>(new StubGitHubUserTokens());
                services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubGitHubApi(GitHubApi));
            },
            settings: new Dictionary<string, string?>
            {
                ["Spark:Mcp:Quota:AnonymousPerMinute"] = "0",
                ["Spark:Mcp:Quota:SignedInPerMinute"] = "0",
            });
        _ = Factory.CreateClient();
        await SeedAsync();
        WaitForIndexing(Store);
    }

    public async Task DisposeAsync()
    {
        try { await Factory.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[CoverageMcpBudgetFixture] host disposal faulted: {ex}"); }
        try { Store?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[CoverageMcpBudgetFixture] store disposal faulted: {ex}"); }
        try { base.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[CoverageMcpBudgetFixture] RavenDB test-driver disposal faulted: {ex}"); }
    }

    private async Task SeedAsync()
    {
        var random = new Random(9);
        string Text(int length) => new(Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26))).ToArray());
        string Sha(int n) => n.ToString("x").PadLeft(40, '0');

        // 736 files of the largest report, paths of 50 to 130 characters, and the 2,400-line file of alternating coverage.
        var files = Enumerable.Range(0, 736)
            .Select(i => (Path: $"libs/module-{i % 40:D2}/MintPlayer.Module{i % 40:D2}/{string.Join('/', Enumerable.Repeat("folder", 1 + i % 6))}/File{i:D3}{Text(i % 20)}.cs",
                Coverable: 20 + i * 37 % 100, Covered: (20 + i * 37 % 100) / 2, Ranges: 1 + i % 9))
            .Append((Path: BigFile, Coverable: 2400, Covered: 1200, Ranges: 1200))
            .ToList();

        await using var bulk = Store.BulkInsert();

        for (var i = 0; i < 179; i++)
        {
            await StoreRepositoryAsync(bulk, 9400 + i, $"repository-{i:D3}-{Text(40)}", latestSha: Sha(100_000 + i));
        }
        await StoreRepositoryAsync(bulk, MonorepoId, Monorepo, latestSha: Sha(39));

        // 40 commits on main, a chain; every third lowers coverage in 8 files.
        var previous = files;
        for (var c = 0; c < 40; c++)
        {
            var current = c % 3 == 2
                ? previous.Select((f, i) => i < 8 ? f with { Covered = Math.Max(0, f.Covered - 5) } : f).ToList()
                : previous;
            await StoreCommitAsync(Sha(c), "main", DateTimeOffset.Parse("2026-08-01T10:00:00Z").AddHours(c), current,
                message: $"Commit {c}: {Text(300)}", parentSha: c == 0 ? null : Sha(c - 1),
                delta: c % 3 == 2 ? -0.4 : 0.1, fileCoverage: c == 39);
            previous = current;
        }

        // 25 covered branches with long names.
        for (var b = 0; b < 25; b++)
        {
            await StoreCommitAsync(Sha(1000 + b), $"feature/{b:D2}-{Text(80)}", DateTimeOffset.Parse("2026-09-01T10:00:00Z").AddHours(b),
                files.Take(5).ToList(), message: Text(200), parentSha: Sha(39), delta: 0, fileCoverage: false);
        }

        // The pull request: 45 files changed, 60 added hunks each, every added line uncovered.
        var changed = files.Take(45).Select(f => f.Path).ToList();
        var headSha = Sha(5000);
        await StoreCommitAsync(headSha, "feature/the-big-change", DateTimeOffset.Parse("2026-09-10T10:00:00Z"),
            files.Take(45).Select(f => f with { Coverable = 240, Covered = 120, Ranges = 60 }).ToList(), message: Text(200), parentSha: Sha(39),
            delta: -1, fileCoverage: true, pullRequest: PullRequest, patch: new PatchCoverage
            {
                DiffBaseRef = Sha(39), MergeBaseSha = Sha(39), LinesCovered = 45 * 60, LinesCoverable = 45 * 180, FilesInDiff = 45, FilesMatched = 45,
            });
        GitHubApi.Comparisons[$"{Owner}/{Monorepo}/compare/{Sha(39)}...{headSha}"] = new JsonObject
        {
            ["merge_base_commit"] = new JsonObject { ["sha"] = Sha(39) },
            ["files"] = new JsonArray([.. changed.Select(path => (JsonNode)new JsonObject
            {
                ["filename"] = path,
                ["status"] = "modified",
                // 60 hunks of 3 added lines each, starting on the uncovered runs of CoverageMcpSeed.Lines(240, 120, 60).
                ["patch"] = string.Join('\n', Enumerable.Range(0, 60).Select(h => $"@@ -{h * 3 + 1},0 +{h * 3 + 1},3 @@\n+a\n+b\n+c")),
            })]),
        }.ToJsonString();

        async Task StoreRepositoryAsync(Raven.Client.Documents.BulkInsert.BulkInsertOperation into, long id, string name, string latestSha)
            => await into.StoreAsync(new Repository
            {
                GitHubId = id,
                Provider = EForgeProvider.GitHub,
                Name = name,
                FullName = $"{Owner}/{name}",
                OwnerLogin = Owner,
                IsPrivate = false,
                DefaultBranch = "main",
                PreviousFullNames = [],
                LatestCoverage = new CoverageSummary { LinesCovered = 26_714, LinesCoverable = 31_942, BranchesCovered = 1633, BranchesTotal = 2595, FilesCount = 736 },
                LatestCoverageSha = latestSha,
            }, Repository.DocumentId(EForgeProvider.GitHub, id));

        async Task StoreCommitAsync(string sha, string branch, DateTimeOffset date, List<(string Path, int Coverable, int Covered, int Ranges)> tree,
            string message, string? parentSha, double delta, bool fileCoverage, int? pullRequest = null, PatchCoverage? patch = null)
        {
            var commitId = Commit.DocumentId(EForgeProvider.GitHub, MonorepoId, sha);
            var buildId = Build.DocumentId(EForgeProvider.GitHub, MonorepoId, sha, runId: 1, runAttempt: 1);
            await bulk.StoreAsync(new Commit
            {
                Repository = Repository.DocumentId(EForgeProvider.GitHub, MonorepoId),
                Sha = sha,
                Branch = branch,
                PullRequestNumber = pullRequest,
                ParentSha = parentSha,
                Message = message,
                AuthoredAt = date,
                Coverage = new CoverageSummary { LinesCovered = tree.Sum(f => f.Covered), LinesCoverable = tree.Sum(f => f.Coverable), FilesCount = tree.Count },
                CoverageDeltaVsParent = delta,
                LatestBuildId = buildId,
            }, commitId);
            await bulk.StoreAsync(new Build { Commit = commitId, CiRunId = 1, CiRunAttempt = 1, Status = "Finalized", CreatedAtUtc = date.UtcDateTime, Patch = patch }, buildId);
            await bulk.StoreAsync(new BuildTreeSummary
            {
                BuildId = buildId,
                Files = [.. tree.Select(f => new TreeFileSummary { Path = f.Path, LinesCovered = f.Covered, LinesCoverable = f.Coverable })],
            }, BuildTreeSummary.DocumentId(buildId));
            if (!fileCoverage)
                return;
            foreach (var file in tree)
            {
                await bulk.StoreAsync(new FileCoverage
                {
                    BuildId = buildId,
                    Path = file.Path,
                    Lines = CoverageMcpSeed.Lines(file.Coverable, file.Covered, file.Ranges),
                    Branches = file.Path == BigFile
                        ? [.. Enumerable.Range(1, 300).Select(line => new LineBranchCoverage { Line = line * 2, Arity = 4, Floor = 1 })]
                        : [new LineBranchCoverage { Line = 1, Arity = 2, Floor = 1 }],
                }, FileCoverage.DocumentId(buildId, file.Path));
            }
        }
    }
}
