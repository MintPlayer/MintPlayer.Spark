using CodeCoverage.Entities;
using CodeCoverage.Services;
using FluentAssertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The base resolver decides which commit a partial upload compares against
/// (docs/coverage-analyzer-suite.md §1): declared sha exactly, else a bounded
/// walk down the covered default-branch commits, else none — and a candidate
/// only counts when its build's tree summary is still on disk, because merged
/// PRs get their build data deleted while the commit keeps its display summary.
/// </summary>
public class BaseResolverTests : CoverageRavenTest
{
    private const long RepoGitHubId = 42;

    private static Repository Repo(string? defaultBranch = "master") => new()
    {
        // A production Repository always arrives loaded from the session, id set.
        Id = Repository.DocumentId(RepoGitHubId),
        GitHubId = RepoGitHubId,
        Name = "repo",
        FullName = "acme/repo",
        OwnerLogin = "acme",
        DefaultBranch = defaultBranch,
    };

    /// <summary>Stores a covered commit; the tree summary is written unless <paramref name="treeDeleted"/>.</summary>
    private static async Task<Commit> SeedCovered(IDocumentStore store, string sha, string branch, DateTimeOffset authoredAt, bool treeDeleted = false)
    {
        using var session = store.OpenAsyncSession();
        var buildId = Build.DocumentId(RepoGitHubId, sha, runId: 1, runAttempt: 1);
        var commit = new Commit
        {
            Sha = sha,
            Repository = Repository.DocumentId(RepoGitHubId),
            Branch = branch,
            AuthoredAt = authoredAt,
            Coverage = new CoverageSummary { LinesCovered = 5, LinesCoverable = 10 },
            LatestBuildId = buildId,
        };
        await session.StoreAsync(commit, Commit.DocumentId(RepoGitHubId, sha));
        if (!treeDeleted)
            await session.StoreAsync(new BuildTreeSummary { BuildId = buildId }, BuildTreeSummary.DocumentId(buildId));
        await session.SaveChangesAsync();
        return commit;
    }

    private static async Task<Commit> SeedUncovered(IDocumentStore store, string sha, string branch)
    {
        using var session = store.OpenAsyncSession();
        var commit = new Commit
        {
            Sha = sha,
            Repository = Repository.DocumentId(RepoGitHubId),
            Branch = branch,
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
        };
        await session.StoreAsync(commit, Commit.DocumentId(RepoGitHubId, sha));
        await session.SaveChangesAsync();
        return commit;
    }

    private async Task<ResolvedBase> Resolve(IDocumentStore store, Commit head, string? declared, string? defaultBranch = "master")
    {
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var resolver = new BaseResolver(session, new ScriptedDiffService());
        return await resolver.ResolveAsync(Repo(defaultBranch), head, declared, CancellationToken.None);
    }

    [Fact]
    public async Task Declared_base_with_live_data_resolves_exactly()
    {
        using var store = GetDocumentStore();
        await SeedCovered(store, "base000", "master", DateTimeOffset.UtcNow.AddHours(-2));
        var head = await SeedUncovered(store, "head000", "feature");

        var resolved = await Resolve(store, head, "base000");

        resolved.Mode.Should().Be(ResolvedBase.Exact);
        resolved.ResolvedSha.Should().Be("base000");
        resolved.RequestedSha.Should().Be("base000");
        resolved.BaseBuildId.Should().NotBeNull();
        resolved.CodeCoverage.Should().NotBeNull();
    }

    [Fact]
    public async Task Declared_base_whose_tree_was_deleted_falls_back_to_the_walk_and_says_so()
    {
        using var store = GetDocumentStore();
        // The declared base: a merged PR's commit — summary kept, tree deleted.
        await SeedCovered(store, "merged0", "feature", DateTimeOffset.UtcNow.AddHours(-1), treeDeleted: true);
        await SeedCovered(store, "master0", "master", DateTimeOffset.UtcNow.AddHours(-3));
        var head = await SeedUncovered(store, "head000", "feature");

        var resolved = await Resolve(store, head, "merged0");

        resolved.Mode.Should().Be(ResolvedBase.Walked);
        resolved.ResolvedSha.Should().Be("master0");
        resolved.RequestedSha.Should().Be("merged0", "the substitution must stay visible");
    }

    [Fact]
    public async Task Unknown_declared_base_walks_to_the_newest_covered_default_branch_commit()
    {
        using var store = GetDocumentStore();
        await SeedCovered(store, "old0000", "master", DateTimeOffset.UtcNow.AddDays(-2));
        await SeedCovered(store, "new0000", "master", DateTimeOffset.UtcNow.AddHours(-1));
        var head = await SeedUncovered(store, "head000", "feature");

        var resolved = await Resolve(store, head, "gone000");

        resolved.Mode.Should().Be(ResolvedBase.Walked);
        resolved.ResolvedSha.Should().Be("new0000");
    }

    [Fact]
    public async Task The_walk_skips_covered_commits_whose_tree_was_deleted()
    {
        using var store = GetDocumentStore();
        await SeedCovered(store, "gone000", "master", DateTimeOffset.UtcNow.AddHours(-1), treeDeleted: true);
        await SeedCovered(store, "live000", "master", DateTimeOffset.UtcNow.AddHours(-2));
        var head = await SeedUncovered(store, "head000", "feature");

        var resolved = await Resolve(store, head, declared: null);

        resolved.Mode.Should().Be(ResolvedBase.Walked);
        resolved.ResolvedSha.Should().Be("live000");
    }

    [Fact]
    public async Task The_walk_never_resolves_the_head_itself()
    {
        using var store = GetDocumentStore();
        // Head is itself a covered default-branch commit — the only one.
        var head = await SeedCovered(store, "head000", "master", DateTimeOffset.UtcNow);

        var resolved = await Resolve(store, head, declared: null);

        resolved.Mode.Should().Be(ResolvedBase.None);
        resolved.ResolvedSha.Should().BeNull();
    }

    [Fact]
    public async Task Nothing_usable_resolves_to_none_not_an_error()
    {
        using var store = GetDocumentStore();
        var head = await SeedUncovered(store, "head000", "feature");

        var resolved = await Resolve(store, head, "gone000");

        resolved.Mode.Should().Be(ResolvedBase.None);
        resolved.RequestedSha.Should().Be("gone000");
        resolved.BaseBuildId.Should().BeNull();
    }

    [Fact]
    public async Task The_compare_api_merge_base_beats_the_walk_when_the_declared_base_is_unusable()
    {
        using var store = GetDocumentStore();
        // The true merge base is older than the walk's favourite — the walk
        // would pick newest0, the compare API knows better.
        await SeedCovered(store, "mbase00", "master", DateTimeOffset.UtcNow.AddDays(-3));
        await SeedCovered(store, "newest0", "master", DateTimeOffset.UtcNow.AddHours(-1));
        var head = await SeedUncovered(store, "head000", "feature");

        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var resolver = new BaseResolver(session, new ScriptedDiffService(new CommitComparison("mbase00", [], false)));
        var resolved = await resolver.ResolveAsync(Repo(), head, "gone000", CancellationToken.None);

        resolved.Mode.Should().Be(ResolvedBase.MergeBase);
        resolved.ResolvedSha.Should().Be("mbase00");
        resolved.RequestedSha.Should().Be("gone000");
    }

    [Fact]
    public async Task An_unusable_merge_base_still_falls_through_to_the_walk()
    {
        using var store = GetDocumentStore();
        await SeedCovered(store, "mbase00", "master", DateTimeOffset.UtcNow.AddDays(-3), treeDeleted: true);
        await SeedCovered(store, "newest0", "master", DateTimeOffset.UtcNow.AddHours(-1));
        var head = await SeedUncovered(store, "head000", "feature");

        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var resolver = new BaseResolver(session, new ScriptedDiffService(new CommitComparison("mbase00", [], false)));
        var resolved = await resolver.ResolveAsync(Repo(), head, null, CancellationToken.None);

        resolved.Mode.Should().Be(ResolvedBase.Walked);
        resolved.ResolvedSha.Should().Be("newest0");
    }

    [Fact]
    public async Task Without_a_default_branch_the_walk_uses_the_heads_own_branch()
    {
        using var store = GetDocumentStore();
        await SeedCovered(store, "feat000", "feature", DateTimeOffset.UtcNow.AddHours(-1));
        var head = await SeedUncovered(store, "head000", "feature");

        var resolved = await Resolve(store, head, declared: null, defaultBranch: null);

        resolved.Mode.Should().Be(ResolvedBase.Walked);
        resolved.ResolvedSha.Should().Be("feat000");
    }
}
