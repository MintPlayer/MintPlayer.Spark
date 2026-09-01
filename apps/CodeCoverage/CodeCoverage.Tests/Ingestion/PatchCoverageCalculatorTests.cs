using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using FluentAssertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Patch coverage = the diff's added lines classified by the head build's own
/// per-line FileCoverage. Pinned: partials count as hits, unmeasured diff
/// files are skipped (not zeroed), unmentioned lines are non-executable, and
/// every unavailability degrades to null rather than failing the finalize.
/// </summary>
public class PatchCoverageCalculatorTests : CoverageRavenTest
{
    private const long RepoGitHubId = 77;
    private const string HeadSha = "aaaa000";
    private const string BaseSha = "bbbb000";

    private static async Task<(Build Build, Commit Commit)> Seed(IDocumentStore store, params FileCoverage[] files)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoGitHubId,
            Name = "repo",
            FullName = "acme/repo",
            OwnerLogin = "acme",
        }, Repository.DocumentId(RepoGitHubId));

        var commit = new Commit { Sha = HeadSha, Repository = Repository.DocumentId(RepoGitHubId), FirstSeenAtUtc = DateTimeOffset.UtcNow };
        await session.StoreAsync(commit, Commit.DocumentId(RepoGitHubId, HeadSha));

        var buildId = Build.DocumentId(RepoGitHubId, HeadSha, 1, 1);
        var build = new Build { Commit = commit.Id, CiRunId = 1, CiRunAttempt = 1, DeclaredBaseSha = BaseSha, CreatedAtUtc = DateTime.UtcNow };
        await session.StoreAsync(build, buildId);

        foreach (var file in files)
            await session.StoreAsync(file, FileCoverage.DocumentId(buildId, file.Path));

        await session.SaveChangesAsync();
        return (build, commit);
    }

    private static FileCoverage File(string path, params (int Number, LineStatus Status)[] lines) => new()
    {
        Path = path,
        Lines = [.. lines.Select(l => new LineCoverage { Number = l.Number, Status = l.Status })],
    };

    private static async Task<PatchCoverage?> Compute(IDocumentStore store, Build build, Commit commit, CommitComparison? comparison)
    {
        using var session = store.OpenAsyncSession();
        var freshBuild = await session.LoadAsync<Build>(build.Id);
        var freshCommit = await session.LoadAsync<Commit>(commit.Id);
        return await PatchCoverageCalculator.ComputeAsync(session, new ScriptedDiffService(comparison), freshBuild, freshCommit, CancellationToken.None);
    }

    [Fact]
    public async Task Added_lines_are_classified_by_the_head_builds_own_line_data()
    {
        using var store = GetDocumentStore();
        var (build, commit) = await Seed(store, File("libs/a/x.ts",
            (1, LineStatus.Covered), (2, LineStatus.NotCovered), (3, LineStatus.PartiallyCovered)));

        var patch = await Compute(store, build, commit, new CommitComparison(BaseSha,
            [new DiffFile("libs/a/x.ts", "modified", null, [1, 2, 3, 4])], false));

        patch.Should().NotBeNull();
        // Line 4 is unmentioned by the report: non-executable, in neither count.
        patch!.LinesCoverable.Should().Be(3);
        // Covered + partially-covered count as hits (the Codecov formula).
        patch.LinesCovered.Should().Be(2);
        patch.FilesMatched.Should().Be(1);
        patch.MergeBaseSha.Should().Be(BaseSha);
    }

    [Fact]
    public async Task A_diff_file_the_build_never_measured_is_skipped_not_zeroed()
    {
        using var store = GetDocumentStore();
        var (build, commit) = await Seed(store, File("libs/a/x.ts", (1, LineStatus.Covered)));

        var patch = await Compute(store, build, commit, new CommitComparison(BaseSha,
            [
                new DiffFile("libs/a/x.ts", "modified", null, [1]),
                new DiffFile("apps/vue/main.ts", "modified", null, [10, 11, 12]),
            ], false));

        patch!.LinesCoverable.Should().Be(1, "the unaffected project's lines must not read as misses");
        patch.LinesCovered.Should().Be(1);
        patch.FilesInDiff.Should().Be(2);
        patch.FilesMatched.Should().Be(1);
    }

    [Fact]
    public async Task No_diff_base_means_no_patch_verdict()
    {
        using var store = GetDocumentStore();
        var (build, commit) = await Seed(store, File("libs/a/x.ts", (1, LineStatus.Covered)));
        using var session = store.OpenAsyncSession();
        var freshBuild = await session.LoadAsync<Build>(build.Id);
        freshBuild.DeclaredBaseSha = null;
        var freshCommit = await session.LoadAsync<Commit>(commit.Id);

        var patch = await PatchCoverageCalculator.ComputeAsync(session, new ScriptedDiffService(), freshBuild, freshCommit, CancellationToken.None);

        patch.Should().BeNull();
    }

    [Fact]
    public async Task No_api_access_means_no_patch_verdict()
    {
        using var store = GetDocumentStore();
        var (build, commit) = await Seed(store, File("libs/a/x.ts", (1, LineStatus.Covered)));

        var patch = await Compute(store, build, commit, comparison: null);

        patch.Should().BeNull();
    }

    [Fact]
    public async Task Truncation_is_carried_so_the_verdict_can_disclose_it()
    {
        using var store = GetDocumentStore();
        var (build, commit) = await Seed(store, File("libs/a/x.ts", (1, LineStatus.Covered)));

        var patch = await Compute(store, build, commit, new CommitComparison(BaseSha,
            [new DiffFile("libs/a/x.ts", "modified", null, [1])], Truncated: true));

        patch!.DiffTruncated.Should().BeTrue();
    }
}
