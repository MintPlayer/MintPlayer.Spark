using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// What fork-contributed coverage may and may not move.
/// </summary>
/// <remarks>
/// <para>
/// The design claims that a fork upload cannot affect anything outside its own pull request. The
/// claim rests on <c>Commit.ContributedFromFork</c> being honoured in two specific places —
/// <c>CommitAssembler.Promote</c> and <c>BaseResolver</c>'s usability chokepoint — and not on the
/// <c>pr/{n}/</c> document-id shape, which no query parses. These tests exercise the two places.
/// </para>
/// <para>
/// ⚠️ Each guard is paired with its negative: the same scenario with the flag cleared must promote
/// or resolve. Without that pair a guard that refused everything unconditionally would pass, and
/// "no coverage ever appears" is a failure mode this feature could plausibly ship with.
/// </para>
/// </remarks>
public class ForkContributionTests : CoverageRavenTest
{
    private const long RepoId = 77;
    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, RepoId);

    private const string Lcov = "SF:/w/src/a.cs\nDA:1,1\nDA:2,1\nend_of_record\n";
    private static readonly string FileList = $"{new string('a', 40)} src/a.cs";

    private static async Task SeedRepository(IDocumentStore store, string? defaultBranch, CoverageSummary? latest = null)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = "repo",
            FullName = "org/repo",
            OwnerLogin = "org",
            DefaultBranch = defaultBranch,
            LatestCoverage = latest,
        }, RepositoryId);
        await seed.SaveChangesAsync();
    }

    /// <summary>Seeds a commit plus one finalized, complete build, and returns the commit document id.</summary>
    private static async Task<string> SeedCoveredCommit(
        IDocumentStore store, string sha, string branch, bool fromFork, int? pullRequestNumber = null, long runId = 1)
    {
        var prSegment = fromFork ? pullRequestNumber : null;
        var commitId = Commit.DocumentId(EForgeProvider.GitHub, RepoId, sha, prSegment);
        var buildId = Build.DocumentId(EForgeProvider.GitHub, RepoId, sha, runId, 1, prSegment);
        var sessionId = $"s{runId}";
        var reportName = UploadAttachments.ReportName(sessionId, 0, "lcov.info");

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Commit
            {
                Repository = RepositoryId,
                Sha = sha,
                Branch = branch,
                PullRequestNumber = pullRequestNumber,
                ContributedFromFork = fromFork,
                AuthoredAt = DateTimeOffset.UtcNow.AddMinutes(-runId),
            }, commitId);

            var build = new Build
            {
                Commit = commitId,
                CiRunId = runId,
                CiRunAttempt = 1,
                Run = Build.ComposeRun(runId, 1),
                CreatedAtUtc = DateTime.UtcNow,
                Sessions = [new BuildSession { SessionId = sessionId, RootDir = "/w", RawFileNames = [reportName] }],
            };
            await seed.StoreAsync(build, buildId);
            seed.Advanced.Attachments.Store(build, reportName, new MemoryStream(Encoding.UTF8.GetBytes(Lcov)));
            seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId), new MemoryStream(Encoding.UTF8.GetBytes(FileList)));
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
        {
            var services = new ServiceCollection()
                .AddSingleton(store).AddSingleton(session)
                .AddSingleton<ICoverageParserFactory, CoverageParserFactory>()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider();
            await ActivatorUtilities.CreateInstance<ParseSessionRecipient>(services)
                .HandleAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId });
        }

        using (var session = store.OpenAsyncSession())
        {
            var build = await session.LoadAsync<Build>(buildId);
            await BuildFinalizer.Finalize(session, new ScriptedDiffService(), build, "Explicit", CancellationToken.None);
            await session.SaveChangesAsync();
        }

        return commitId;
    }

    private async Task Assemble(IDocumentStore store, string commitId)
    {
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        await CommitAssemblerTests.CreateAssembler(store, session, new ScriptedDiffService()).AssembleAsync(commitId);
        await session.SaveChangesAsync();
        WaitForIndexing(store);
    }

    private static async Task<Repository> LoadRepository(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<Repository>(RepositoryId);
    }

    // ── Promotion ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fork_coverage_never_becomes_the_repository_headline()
    {
        using var store = GetDocumentStore();
        await SeedRepository(store, defaultBranch: "master");

        // The fork's branch is literally the default branch name — the ordinary case for a
        // contributor who forked and committed on main. The guard must not depend on the name.
        var commitId = await SeedCoveredCommit(store, new string('f', 40), "master", fromFork: true, pullRequestNumber: 42);
        await Assemble(store, commitId);

        var repository = await LoadRepository(store);
        Assert.Null(repository.LatestCoverage);
        Assert.Null(repository.LatestCoverageSha);
    }

    [Fact]
    public async Task The_same_commit_promotes_when_it_is_not_from_a_fork()
    {
        using var store = GetDocumentStore();
        await SeedRepository(store, defaultBranch: "master");

        var sha = new string('e', 40);
        var commitId = await SeedCoveredCommit(store, sha, "master", fromFork: false);
        await Assemble(store, commitId);

        var repository = await LoadRepository(store);
        Assert.NotNull(repository.LatestCoverage);
        Assert.Equal(sha, repository.LatestCoverageSha);
    }

    [Fact]
    public async Task A_never_covered_repository_no_longer_promotes_a_non_default_branch()
    {
        using var store = GetDocumentStore();
        // LatestCoverage null AND a known default branch: the escape that used to let any branch
        // through, and the one an anonymous fork upload would have reached first.
        await SeedRepository(store, defaultBranch: "master", latest: null);

        var commitId = await SeedCoveredCommit(store, new string('b', 40), "feature/x", fromFork: false);
        await Assemble(store, commitId);

        var repository = await LoadRepository(store);
        Assert.Null(repository.LatestCoverage);
    }

    [Fact]
    public async Task An_unknown_default_branch_still_promotes_so_a_repository_is_never_badge_less()
    {
        using var store = GetDocumentStore();
        await SeedRepository(store, defaultBranch: null);

        var commitId = await SeedCoveredCommit(store, new string('c', 40), "feature/x", fromFork: false);
        await Assemble(store, commitId);

        var repository = await LoadRepository(store);
        Assert.NotNull(repository.LatestCoverage);
    }

    // ── Baseline ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fork_coverage_is_never_selected_as_a_comparison_baseline()
    {
        using var store = GetDocumentStore();
        await SeedRepository(store, defaultBranch: "master");

        // A covered fork commit on the default branch name, assembled so it is genuinely usable —
        // it is rejected for its provenance, not for being unusable.
        var forkCommitId = await SeedCoveredCommit(store, new string('f', 40), "master", fromFork: true, pullRequestNumber: 9, runId: 1);
        await Assemble(store, forkCommitId);

        var head = new Commit { Repository = RepositoryId, Sha = new string('9', 40), Branch = "master" };
        using var session = store.OpenAsyncSession();
        var resolver = ActivatorUtilities.CreateInstance<BaseResolver>(
            new ServiceCollection()
                .AddSingleton(store).AddSingleton(session)
                .AddSingleton<IForgeIntegrationResolver>(new ScriptedDiffService())
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider());

        var resolved = await resolver.ResolveAsync(await LoadRepository(store), head, declaredBaseSha: null, CancellationToken.None);

        Assert.Equal(ResolvedBase.None, resolved.Mode);
        Assert.Null(resolved.ResolvedSha);
    }

    [Fact]
    public async Task An_equivalent_first_party_commit_IS_selected_as_a_baseline()
    {
        using var store = GetDocumentStore();
        await SeedRepository(store, defaultBranch: "master");

        var baseSha = new string('a', 40);
        var baseCommitId = await SeedCoveredCommit(store, baseSha, "master", fromFork: false, runId: 1);
        await Assemble(store, baseCommitId);

        var head = new Commit { Repository = RepositoryId, Sha = new string('9', 40), Branch = "master" };
        using var session = store.OpenAsyncSession();
        var resolver = ActivatorUtilities.CreateInstance<BaseResolver>(
            new ServiceCollection()
                .AddSingleton(store).AddSingleton(session)
                .AddSingleton<IForgeIntegrationResolver>(new ScriptedDiffService())
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider());

        var resolved = await resolver.ResolveAsync(await LoadRepository(store), head, declaredBaseSha: null, CancellationToken.None);

        Assert.Equal(baseSha, resolved.ResolvedSha);
    }

    // ── Provenance ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_fork_upload_without_a_pull_request_number_is_refused_rather_than_stored_as_first_party()
    {
        var request = new UploadIngestRequest(
            Repository: new Repository { GitHubId = RepoId },
            CommitSha: new string('a', 40),
            Branch: "feature/x", PullRequestNumber: null, BaseRef: null, PrBaseSha: null, ParentSha: null,
            RunId: 1, RunAttempt: 1, Workflow: null, EventName: null, JobName: null, Flags: null,
            RootDir: null, FileList: null, Partial: false, CarryForward: null, BaseSha: null,
            Files: [], ContributedFromFork: true);

        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var ingestor = ActivatorUtilities.CreateInstance<UploadIngestor>(
            new ServiceCollection()
                .AddSingleton(session)
                .AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(new SilentMessageBus())
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider());

        await Assert.ThrowsAsync<ArgumentException>(() => ingestor.IngestAsync(request));
    }

    /// <summary>The ingestor never reaches a broadcast in these tests; this only satisfies the ctor.</summary>
    private sealed class SilentMessageBus : MintPlayer.Spark.Messaging.Abstractions.IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void Fork_and_first_party_document_ids_cannot_collide_for_the_same_sha()
    {
        var sha = new string('a', 40);

        var firstParty = Commit.DocumentId(EForgeProvider.GitHub, RepoId, sha);
        var fork = Commit.DocumentId(EForgeProvider.GitHub, RepoId, sha, 42);

        Assert.NotEqual(firstParty, fork);
        Assert.Contains("/pr/42/", fork, StringComparison.Ordinal);
        Assert.DoesNotContain("/pr/", firstParty, StringComparison.Ordinal);
    }
}
