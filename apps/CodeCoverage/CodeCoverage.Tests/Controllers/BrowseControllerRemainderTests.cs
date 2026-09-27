using System.Text.Json;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Indexes;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The Browse endpoints <see cref="BrowseControllerTests"/> does not reach: the account pages,
/// the commit listing, branches, the hierarchy chart, the flag tree, the rejected-report banner,
/// the tree's fallback for builds that predate tree summaries, and the file viewer.
/// </summary>
/// <remarks>
/// Every listing here excludes fork-contributed commits and hides private repositories from a
/// viewer who does not manage their owner, so those rules get a case wherever the endpoint
/// applies them.
/// </remarks>
public class BrowseControllerRemainderTests : CoverageRavenTest
{
    private const long PublicRepo = 8801;
    private const long PrivateRepo = 8802;
    private const string Sha = "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567";

    private static string RepoId(long id) => Repository.DocumentId(EForgeProvider.GitHub, id);

    private static string CommitId(string sha) => Commit.DocumentId(EForgeProvider.GitHub, PublicRepo, sha);

    private static string BuildId(string sha) => Build.DocumentId(EForgeProvider.GitHub, PublicRepo, sha, 1, 1);

    private static BrowseController CreateController(IAsyncDocumentSession session, params string[] owners)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        var forge = ScriptedForge.From(new(owners, GitHubTokenState.Ok));
        forge.Files["src/app.ts"] = "export const x = 1;";
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddScoped<IRepositoryResolver>(sp => new TestRepositoryResolver(sp.GetService<IAsyncDocumentSession>()));
        services.AddScoped<BrowseController>();
        var controller = services.BuildServiceProvider().GetRequiredService<BrowseController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static Commit CommitOf(string sha, string? branch, DateTimeOffset at, bool covered = true, bool fork = false) => new()
    {
        Repository = RepoId(PublicRepo),
        Sha = sha,
        Branch = branch,
        AuthoredAt = at,
        ContributedFromFork = fork,
        Coverage = covered ? new CoverageSummary { LinesCovered = 5, LinesCoverable = 10 } : null,
        LatestBuildId = BuildId(sha),
    };

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Account { GitHubId = 1, Login = "acme" }, Account.DocumentId(EForgeProvider.GitHub, 1));
        await seed.StoreAsync(new Account { GitHubId = 1, Login = "acme", Provider = EForgeProvider.GitLab },
            Account.DocumentId(EForgeProvider.GitLab, 1));
        await seed.StoreAsync(new Repository
        {
            GitHubId = PublicRepo, Name = "Zeta", FullName = "acme/Zeta", OwnerLogin = "acme", IsPrivate = false,
            DefaultBranch = "main", BadgeToken = "badge-secret",
        }, RepoId(PublicRepo));
        await seed.StoreAsync(new Repository
        {
            GitHubId = PrivateRepo, Name = "alpha", FullName = "acme/alpha", OwnerLogin = "acme", IsPrivate = true,
        }, RepoId(PrivateRepo));

        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var commit in new[]
                 {
                     CommitOf(Sha, "main", t0.AddHours(3)),
                     CommitOf("1111111111111111111111111111111111111111", "feature", t0.AddHours(2)),
                     CommitOf("2222222222222222222222222222222222222222", "main", t0.AddHours(1), covered: false),
                     CommitOf("3333333333333333333333333333333333333333", "their-branch", t0.AddHours(4), fork: true),
                     CommitOf("4444444444444444444444444444444444444444", null, t0),
                 })
            await seed.StoreAsync(commit, CommitId(commit.Sha));

        await seed.SaveChangesAsync();
    }

    private static T Ok<T>(ActionResult<T> result) => (T)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task An_anonymous_viewer_sees_only_the_accounts_public_repositories_without_management_data()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var repos = Ok(await CreateController(session).GetAccountRepos("github", "acme", default)).ToList();

        repos.Select(r => r.FullName).Should().Equal("acme/Zeta");
        repos[0].CanManage.Should().BeFalse();
        repos[0].BadgeToken.Should().BeNull("a badge token is management data");
    }

    /// <summary>An owner also sees the private repository, ordered case-insensitively by name, with its badge token.</summary>
    [Fact]
    public async Task An_owner_sees_every_repository_ordered_by_name()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var repos = Ok(await CreateController(session, "acme").GetAccountRepos("github", "acme", default)).ToList();

        repos.Select(r => r.Name).Should().Equal("alpha", "Zeta");
        repos.Single(r => r.Name == "Zeta").BadgeToken.Should().Be("badge-secret");
        repos.Should().OnlyContain(r => r.CanManage);
    }

    [Fact]
    public async Task An_unknown_forge_is_refused_on_every_account_endpoint()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        (await controller.GetAccountRepos("nosuchforge", "acme", default)).Result.Should().BeOfType<JsonResult>();
        (await controller.GetAccount("nosuchforge", "acme", default)).Result.Should().BeOfType<JsonResult>();
        (await controller.GetSparklines("nosuchforge", "acme", default)).Result.Should().BeOfType<JsonResult>();
    }

    /// <summary>The owner key includes the forge, so the same login on two forges resolves to two accounts.</summary>
    [Theory]
    [InlineData("github")]
    [InlineData("gitlab")]
    public async Task An_account_is_found_by_its_forge_qualified_owner_key(string provider)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var account = Ok(await CreateController(session).GetAccount(provider, "acme", default));

        account.Id.Should().Be(Account.DocumentId(ForgeProviders.TryParse(provider, out var p) ? p : default, 1));
        account.Login.Should().Be("acme");
    }

    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        (await CreateController(session).GetAccount("github", "nobody", default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_commit_listing_is_newest_first_and_excludes_fork_contributions()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var commits = Ok(await CreateController(session).GetCommits("github", "acme", "Zeta", branch: null, take: 1000)).ToList();

        commits.Select(c => c.Sha[..4]).Should().Equal("0a1b", "1111", "2222", "4444");
        commits[0].Coverage!.LinesCoverable.Should().Be(10);
    }

    [Fact]
    public async Task The_commit_listing_narrows_by_branch_coverage_and_page()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        Ok(await controller.GetCommits("github", "acme", "Zeta", branch: "main")).Select(c => c.Sha[..4])
            .Should().Equal("0a1b", "2222");
        Ok(await controller.GetCommits("github", "acme", "Zeta", branch: "main", withCoverageOnly: true)).Select(c => c.Sha[..4])
            .Should().Equal("0a1b");
        Ok(await controller.GetCommits("github", "acme", "Zeta", branch: null, skip: 1, take: 2)).Select(c => c.Sha[..4])
            .Should().Equal("1111", "2222");
    }

    [Fact]
    public async Task A_private_repositorys_commits_are_refused_to_an_anonymous_viewer()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        (await CreateController(session).GetCommits("github", "acme", "alpha", branch: null)).Result.Should().BeOfType<JsonResult>();
    }

    /// <summary>Branches with coverage only, the default first, no fork head branch and no blank name.</summary>
    [Fact]
    public async Task Branches_list_the_default_first_and_leave_out_fork_and_uncovered_ones()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        Ok(await CreateController(session).GetBranches("github", "acme", "Zeta", default)).Should().Equal("main", "feature");
    }

    private static async Task SeedTreeAsync(IDocumentStore store, bool withSummary, params ReportIngestOutcome[] rejected)
    {
        using var seed = store.OpenAsyncSession();
        var buildId = BuildId(Sha);
        await seed.StoreAsync(new Build
        {
            Commit = CommitId(Sha), CiRunId = 1, CiRunAttempt = 1, Status = "Finalized",
            Sessions = [new BuildSession { SessionId = "s1", ParseStatus = "Parsed", Reports = [.. rejected] }],
        }, buildId);

        var files = new[]
        {
            new FileCoverage
            {
                BuildId = buildId, Path = "src/app.ts",
                Lines = [new LineCoverage { Number = 1, Status = LineStatus.Covered }, new LineCoverage { Number = 2, Status = LineStatus.NotCovered }],
            },
            new FileCoverage
            {
                BuildId = buildId, Path = "src/lib/util.ts",
                Lines = [new LineCoverage { Number = 1, Status = LineStatus.Covered }],
            },
            new FileCoverage { BuildId = buildId, Path = "/elsewhere/gen.ts", Matched = false },
        };
        if (rejected.Length == 0)
            foreach (var file in files)
                await seed.StoreAsync(file, FileCoverage.DocumentId(buildId, file.Path));

        if (withSummary)
            await seed.StoreAsync(new BuildTreeSummary
            {
                BuildId = buildId,
                Files = [.. files.Select(f => new TreeFileSummary
                {
                    Path = f.Path, Matched = f.Matched,
                    LinesCovered = f.Lines.Count(l => l.Status != LineStatus.NotCovered), LinesCoverable = f.Lines.Count,
                })],
            }, BuildTreeSummary.DocumentId(buildId));

        await seed.StoreAsync(new BuildTreeSummary
        {
            BuildId = buildId,
            Files = [new TreeFileSummary { Path = "src/flagged.ts", LinesCovered = 1, LinesCoverable = 4 }],
        }, BuildTreeSummary.FlagDocumentId(buildId, "unit"));

        await seed.SaveChangesAsync();
    }

    /// <summary>A build that predates tree summaries still renders: its files are streamed instead.</summary>
    [Fact]
    public async Task A_build_without_a_tree_summary_falls_back_to_streaming_its_files()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await SeedTreeAsync(store, withSummary: false);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var tree = Ok(await CreateController(session).GetTree("github", "acme", "Zeta", Sha, path: null, flag: null, default));

        tree.Entries.Select(e => e.Name).Should().Equal("src");
        tree.Entries.Single().LinesCoverable.Should().Be(3);
        tree.UnmatchedFiles.Should().Equal("/elsewhere/gen.ts");
    }

    [Fact]
    public async Task A_flag_narrows_the_tree_and_an_unknown_flag_is_empty()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await SeedTreeAsync(store, withSummary: true);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        var flagged = Ok(await controller.GetTree("github", "acme", "Zeta", Sha, path: "src", flag: "unit", default));
        flagged.Entries.Select(e => e.Name).Should().Equal("flagged.ts");

        var unknown = Ok(await controller.GetTree("github", "acme", "Zeta", Sha, path: null, flag: "nope", default));
        unknown.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// An empty tree explains itself: the reports the build rejected are surfaced at the root, so
    /// "nothing measured" is not left unexplained.
    /// </summary>
    [Fact]
    public async Task An_empty_tree_carries_the_reports_the_build_rejected()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await SeedTreeAsync(store, withSummary: false,
            new ReportIngestOutcome { FileName = "broken.xml", Parsed = false, Reason = "Malformed", Detail = "unexpected end" },
            new ReportIngestOutcome { FileName = "fine.info", Parsed = true });
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var tree = Ok(await CreateController(session).GetTree("github", "acme", "Zeta", Sha, path: null, flag: null, default));

        tree.Entries.Should().BeEmpty();
        var rejected = tree.RejectedReports!.Should().ContainSingle().Which;
        rejected.FileName.Should().Be("broken.xml");
        rejected.Reason.Should().Be("Malformed");
        rejected.Detail.Should().Be("unexpected end");
    }

    [Fact]
    public async Task The_hierarchy_nests_matched_files_under_their_folders_with_a_coverage_colour()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await SeedTreeAsync(store, withSummary: true);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var root = Ok(await CreateController(session).GetHierarchy("github", "acme", "Zeta", Sha, default));

        root.Name.Should().Be("Zeta");
        var src = root.Children!.Should().ContainSingle("the unmatched file is left out").Which;
        src.Id.Should().Be("src");
        src.Children!.Select(c => c.Id).Should().Equal("src/app.ts", "src/lib");
        var app = src.Children!.Single(c => c.Id == "src/app.ts");
        app.Value.Should().Be(2);
        app.ColorValue.Should().Be(50.0);
        src.Children!.Single(c => c.Id == "src/lib").Children!.Single().Name.Should().Be("util.ts");
    }

    [Fact]
    public async Task The_hierarchy_of_a_commit_without_a_build_is_not_found()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        (await controller.GetHierarchy("github", "acme", "Zeta", "ffffffffffffffffffffffffffffffffffffffff", default)).Result
            .Should().BeOfType<NotFoundResult>();
        (await controller.GetHierarchy("github", "acme", "alpha", Sha, default)).Result.Should().BeOfType<JsonResult>();
    }

    [Fact]
    public async Task The_file_viewer_returns_the_coverage_and_the_forges_source()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await SeedTreeAsync(store, withSummary: true);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        var result = await controller.GetFile("github", "acme", "Zeta", Sha, "src/app.ts", default);

        var payload = JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value)).RootElement;
        payload.GetProperty("Path").GetString().Should().Be("src/app.ts");
        payload.GetProperty("Source").GetString().Should().Be("export const x = 1;");
        payload.GetProperty("Lines").GetArrayLength().Should().Be(2);

        (await controller.GetFile("github", "acme", "Zeta", Sha, "src/missing.ts", default)).Result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// <see cref="VCommit"/> is only ever materialized as <see cref="Commit"/> by the app's own
    /// queries, so its projection shape was never read back. Projecting pins that the index stores
    /// what its view class declares — a field dropped from the map would come back empty here.
    /// </summary>
    [Fact]
    public async Task The_commit_index_projects_its_view_class()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var view = await session.Query<VCommit, Commits_ByRepository>()
            .Where(c => c.Sha == Sha)
            .ProjectInto<VCommit>()
            .SingleAsync();

        view.Repository.Should().Be(RepoId(PublicRepo));
        view.Branch.Should().Be("main");
        view.HasCoverage.Should().BeTrue();
        view.ContributedFromFork.Should().BeFalse();
        view.Date.HasValue.Should().BeTrue();
    }
}
