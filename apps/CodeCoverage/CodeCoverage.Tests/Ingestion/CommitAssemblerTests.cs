using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The assembler end to end against embedded RavenDB: real parse, real
/// finalize, real base resolution (declared base ⇒ exact), scripted GitHub.
/// Repository 7, default branch master; every fixture commit's files are
/// <c>src/a.cs</c> and <c>src/b.cs</c> unless stated.
/// </summary>
public class CommitAssemblerTests : CoverageRavenTest
{
    private const long RepoId = 7;
    private static readonly string RepositoryId = Repository.DocumentId(RepoId);

    // a.cs: 1 of 2 lines at the base, 2 of 2 when re-measured; b.cs: 3 of 4.
    private const string LcovABase = "SF:/w/src/a.cs\nDA:1,1\nDA:2,0\nend_of_record\n";
    private const string LcovAHead = "SF:/w/src/a.cs\nDA:1,1\nDA:2,1\nend_of_record\n";
    private const string LcovB = "SF:/w/src/b.cs\nDA:1,1\nDA:2,1\nDA:3,1\nDA:4,0\nend_of_record\n";

    private static string Oid(char c) => new(c, 40);
    private static readonly string OidA1 = Oid('a');
    private static readonly string OidA2 = Oid('c');
    private static readonly string OidB1 = Oid('b');
    private static readonly string OidB2 = Oid('d');

    private static string FileList(params (string Path, string Oid)[] entries)
        => string.Join('\n', entries.Select(e => $"{e.Oid} {e.Path}"));

    public static ICommitAssembler CreateAssembler(IDocumentStore store, IAsyncDocumentSession session, IGitHubDiffService diffService)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(session)
            .AddSingleton(diffService)
            .AddSingleton<IBaseResolver, BaseResolver>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<CommitAssembler>(services);
    }

    private static ParseSessionRecipient CreateRecipient(IDocumentStore store, IAsyncDocumentSession session)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(session)
            .AddSingleton<ICoverageParserFactory, CoverageParserFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ParseSessionRecipient>(services);
    }

    private static async Task SeedRepository(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository
        {
            GitHubId = RepoId, Name = "repo", FullName = "org/repo", OwnerLogin = "org", DefaultBranch = "master",
        }, RepositoryId);
        await seed.SaveChangesAsync();
    }

    private static async Task SeedCommit(IDocumentStore store, string sha, string branch, DateTimeOffset date, string? parentSha = null)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Commit
        {
            Repository = RepositoryId, Sha = sha, Branch = branch, AuthoredAt = date, ParentSha = parentSha,
            // As the new action sends it on a push: trusted without an API round-trip.
            ParentShaSource = parentSha is null ? null : "upload",
        }, Commit.DocumentId(RepoId, sha));
        await seed.SaveChangesAsync();
    }

    /// <summary>Seeds one build with one session, parses it, finalizes it. Returns the build id.</summary>
    private static async Task<string> Upload(IDocumentStore store, string sha, long runId, string lcov, string fileList,
        bool partial = false, string? baseSha = null, bool carryForward = true)
    {
        var commitId = Commit.DocumentId(RepoId, sha);
        var buildId = Build.DocumentId(RepoId, sha, runId, 1);
        var sessionId = $"s{runId}";
        var reportName = UploadAttachments.ReportName(sessionId, 0, "lcov.info");

        using (var seed = store.OpenAsyncSession())
        {
            var build = new Build
            {
                Commit = commitId, CiRunId = runId, CiRunAttempt = 1, Run = Build.ComposeRun(runId, 1),
                CreatedAtUtc = DateTime.UtcNow, Partial = partial, DeclaredBaseSha = baseSha, CarryForward = carryForward,
                Sessions = [new BuildSession { SessionId = sessionId, RootDir = "/w", RawFileNames = [reportName] }],
            };
            await seed.StoreAsync(build, buildId);
            seed.Advanced.Attachments.Store(build, reportName, new MemoryStream(Encoding.UTF8.GetBytes(lcov)));
            seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId), new MemoryStream(Encoding.UTF8.GetBytes(fileList)));
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
        {
            await CreateRecipient(store, session).HandleAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId });
        }

        using (var session = store.OpenAsyncSession())
        {
            var build = await session.LoadAsync<Build>(buildId);
            await BuildFinalizer.Finalize(session, new ScriptedDiffService(), build, "Explicit", CancellationToken.None);
            await session.SaveChangesAsync();
        }

        return buildId;
    }

    private async Task<CommitAssembly?> Assemble(IDocumentStore store, string sha, IGitHubDiffService? diffService = null)
    {
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var assembly = await CreateAssembler(store, session, diffService ?? new ScriptedDiffService()).AssembleAsync(Commit.DocumentId(RepoId, sha));
        await session.SaveChangesAsync();
        WaitForIndexing(store);
        return assembly;
    }

    private static async Task<T?> Load<T>(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private static async Task<Dictionary<string, FileCoverage>> AssembledFiles(IDocumentStore store, string sha)
    {
        using var session = store.OpenAsyncSession();
        var files = await session.Advanced.LoadStartingWithAsync<FileCoverage>(CommitAssembly.FilesPrefix(Commit.DocumentId(RepoId, sha)), pageSize: 1024);
        return files.ToDictionary(f => f.Path, StringComparer.Ordinal);
    }

    /// <summary>Master commit "m1": full upload of a.cs (1/2) and b.cs (3/4) with OIDs A1/B1.</summary>
    private async Task SeedAssembledBase(IDocumentStore store, DateTimeOffset date)
    {
        await SeedRepository(store);
        await SeedCommit(store, "m1", "master", date);
        await Upload(store, "m1", runId: 1, LcovABase + LcovB, FileList(("src/a.cs", OidA1), ("src/b.cs", OidB1)));
        await Assemble(store, "m1");
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_full_upload_assembles_to_exactly_the_build_and_promotes_the_repository()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);

        var assembly = await Load<CommitAssembly>(store, CommitAssembly.DocumentId(Commit.DocumentId(RepoId, "m1")));
        var build = await Load<Build>(store, Build.DocumentId(RepoId, "m1", 1, 1));
        var repository = await Load<Repository>(store, RepositoryId);

        assembly.Should().NotBeNull();
        assembly!.Completeness.Should().Be(CommitAssembly.Complete);
        assembly.MeasuredFiles.Should().Be(2);
        assembly.CarriedFiles.Should().Be(0);
        assembly.Coverage.LinesCovered.Should().Be(build!.Coverage!.LinesCovered);
        assembly.Coverage.LinesCoverable.Should().Be(build.Coverage.LinesCoverable);
        repository!.LatestCoverageSha.Should().Be("m1");
        repository.LatestCoverage!.LinesCoverable.Should().Be(6);
    }

    [Fact]
    public async Task A_partial_upload_carries_unchanged_files_from_the_declared_base_and_measured_wins()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p1", "feature", T0.AddHours(1), parentSha: "m1");

        // a.cs changed (A2) and was re-measured (2/2); b.cs unchanged (B1) and not measured; README is not coverable.
        await Upload(store, "p1", runId: 2, LcovAHead,
            FileList(("src/a.cs", OidA2), ("src/b.cs", OidB1), ("README.md", Oid('e'))), partial: true, baseSha: "m1");
        var assembly = await Assemble(store, "p1");

        assembly.Should().NotBeNull();
        assembly!.Completeness.Should().Be(CommitAssembly.Complete);
        assembly.BaseSha.Should().Be("m1");
        assembly.BaseResolution.Should().Be(ResolvedBase.Exact);
        assembly.MeasuredFiles.Should().Be(1);
        assembly.CarriedFiles.Should().Be(1);
        assembly.UnmeasuredFiles.Should().Be(0);
        assembly.Coverage.LinesCoverable.Should().Be(6);
        assembly.Coverage.LinesCovered.Should().Be(5); // a: 2/2 measured here, b: 3/4 carried
        assembly.OldestOriginSha.Should().Be("m1");

        var files = await AssembledFiles(store, "p1");
        files["src/a.cs"].Origin!.Kind.Should().Be(FileOrigin.Measured);
        files["src/a.cs"].BlobOid.Should().Be(OidA2);
        files["src/b.cs"].Origin!.Kind.Should().Be(FileOrigin.Carried);
        files["src/b.cs"].Origin!.FromSha.Should().Be("m1");
        files["src/b.cs"].Origin!.OriginSha.Should().Be("m1");

        var commit = await Load<Commit>(store, Commit.DocumentId(RepoId, "p1"));
        commit!.Coverage!.LinesCovered.Should().Be(5);
        commit.AssemblyCompleteness.Should().Be(CommitAssembly.Complete);

        // A feature-branch assembly never becomes the repository headline.
        (await Load<Repository>(store, RepositoryId))!.LatestCoverageSha.Should().Be("m1");
    }

    [Fact]
    public async Task A_changed_file_the_base_knew_is_not_carried_and_makes_the_assembly_partial()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p2", "feature", T0.AddHours(1));

        // b.cs changed (B2) but only a.cs was measured.
        await Upload(store, "p2", runId: 2, LcovAHead, FileList(("src/a.cs", OidA1), ("src/b.cs", OidB2)), partial: true, baseSha: "m1");
        var assembly = await Assemble(store, "p2");

        assembly!.CarriedFiles.Should().Be(0);
        assembly.UnmeasuredFiles.Should().Be(1);
        assembly.Completeness.Should().Be(CommitAssembly.Partial);
        assembly.IncompleteReasons.Should().Contain(CommitAssembly.ReasonUnmeasuredChanges);
        (await AssembledFiles(store, "p2")).Keys.Should().Equal(["src/a.cs"]);
    }

    [Fact]
    public async Task A_file_absent_from_the_head_list_is_neither_carried_nor_missing()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p3", "feature", T0.AddHours(1));

        // b.cs was deleted on the head: the list no longer names it.
        await Upload(store, "p3", runId: 2, LcovAHead, FileList(("src/a.cs", OidA1)), partial: true, baseSha: "m1");
        var assembly = await Assemble(store, "p3");

        assembly!.CarriedFiles.Should().Be(0);
        assembly.UnmeasuredFiles.Should().Be(0);
        assembly.Completeness.Should().Be(CommitAssembly.Complete);
        assembly.Coverage.FilesCount.Should().Be(1);
    }

    [Fact]
    public async Task Carrying_from_a_commit_that_itself_carried_keeps_the_original_origin()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p1", "feature", T0.AddHours(1));
        await Upload(store, "p1", runId: 2, LcovAHead, FileList(("src/a.cs", OidA2), ("src/b.cs", OidB1)), partial: true, baseSha: "m1");
        await Assemble(store, "p1");

        // Second hop: nothing measured at all, everything unchanged since p1.
        await SeedCommit(store, "p1b", "feature", T0.AddHours(2));
        await Upload(store, "p1b", runId: 3, lcov: "", FileList(("src/a.cs", OidA2), ("src/b.cs", OidB1)), partial: true, baseSha: "p1");
        var assembly = await Assemble(store, "p1b");

        assembly!.MeasuredFiles.Should().Be(0);
        assembly.CarriedFiles.Should().Be(2);
        assembly.Completeness.Should().Be(CommitAssembly.Complete);
        assembly.Coverage.LinesCovered.Should().Be(5);

        var files = await AssembledFiles(store, "p1b");
        files["src/a.cs"].Origin!.FromSha.Should().Be("p1");
        files["src/a.cs"].Origin!.OriginSha.Should().Be("p1");   // measured at p1
        files["src/b.cs"].Origin!.FromSha.Should().Be("p1");
        files["src/b.cs"].Origin!.OriginSha.Should().Be("m1");   // measured at m1, carried twice
        assembly.OldestOriginSha.Should().Be("m1");
    }

    [Fact]
    public async Task A_run_whose_tests_failed_carries_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p4", "feature", T0.AddHours(1));

        await Upload(store, "p4", runId: 2, LcovAHead, FileList(("src/a.cs", OidA1), ("src/b.cs", OidB1)),
            partial: true, baseSha: "m1", carryForward: false);
        var assembly = await Assemble(store, "p4");

        assembly!.MeasuredFiles.Should().Be(1);
        assembly.CarriedFiles.Should().Be(0);
        assembly.Completeness.Should().Be(CommitAssembly.Partial);
        assembly.IncompleteReasons.Should().Contain(CommitAssembly.ReasonTestsFailed);
        assembly.Coverage.LinesCovered.Should().Be(2);
    }

    [Fact]
    public async Task A_v1_file_list_without_oids_carries_nothing_and_says_why()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p5", "feature", T0.AddHours(1));

        await Upload(store, "p5", runId: 2, LcovAHead, "src/a.cs\nsrc/b.cs", partial: true, baseSha: "m1");
        var assembly = await Assemble(store, "p5");

        assembly!.CarriedFiles.Should().Be(0);
        assembly.Completeness.Should().Be(CommitAssembly.Partial);
        assembly.IncompleteReasons.Should().Contain(CommitAssembly.ReasonNoBlobIds);
    }

    [Fact]
    public async Task Without_oids_the_compare_api_decides_what_is_unchanged()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p7", "feature", T0.AddHours(1));

        // v1 list; GitHub says only a.cs changed → b.cs carried, a.cs measured.
        await Upload(store, "p7", runId: 2, LcovAHead, "src/a.cs\nsrc/b.cs", partial: true, baseSha: "m1");
        var github = new ScriptedDiffService(new CommitComparison("m1", [new DiffFile("src/a.cs", "modified", null, [2])], Truncated: false));
        var assembly = await Assemble(store, "p7", github);

        assembly!.CarriedFiles.Should().Be(1);
        assembly.UnmeasuredFiles.Should().Be(0);
        assembly.Completeness.Should().Be(CommitAssembly.Complete);
        assembly.Coverage.LinesCovered.Should().Be(5);
        github.Calls.Should().Contain(("m1", "p7"));
    }

    [Fact]
    public async Task Without_oids_a_changed_unmeasured_file_is_reported_via_the_compare_api()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p8", "feature", T0.AddHours(1));

        await Upload(store, "p8", runId: 2, LcovAHead, "src/a.cs\nsrc/b.cs", partial: true, baseSha: "m1");
        var github = new ScriptedDiffService(new CommitComparison("m1", [new DiffFile("src/b.cs", "modified", null, [1])], Truncated: false));
        var assembly = await Assemble(store, "p8", github);

        assembly!.CarriedFiles.Should().Be(0);
        assembly.UnmeasuredFiles.Should().Be(1);
        assembly.Completeness.Should().Be(CommitAssembly.Partial);
    }

    [Fact]
    public async Task A_truncated_comparison_means_unknown_and_carries_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "p9", "feature", T0.AddHours(1));

        await Upload(store, "p9", runId: 2, LcovAHead, "src/a.cs\nsrc/b.cs", partial: true, baseSha: "m1");
        var github = new ScriptedDiffService(new CommitComparison("m1", [], Truncated: true));
        var assembly = await Assemble(store, "p9", github);

        assembly!.CarriedFiles.Should().Be(0);
        assembly.Completeness.Should().Be(CommitAssembly.Partial);
        assembly.IncompleteReasons.Should().Contain(CommitAssembly.ReasonNoBlobIds);
    }

    [Fact]
    public async Task Deltas_are_null_for_the_first_default_branch_commit_and_stamped_for_its_child()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0); // m1: 4/6 = 66.67%

        var m1 = await Load<Commit>(store, Commit.DocumentId(RepoId, "m1"));
        Assert.Null(m1!.CoverageDeltaVsParent);
        Assert.Null(m1.CoverageDeltaVsDefaultBranch);

        // m2: child of m1, full upload with a.cs now 2/2 → 5/6 = 83.33%.
        await SeedCommit(store, "m2", "master", T0.AddHours(1), parentSha: "m1");
        await Upload(store, "m2", runId: 2, LcovAHead + LcovB, FileList(("src/a.cs", OidA2), ("src/b.cs", OidB1)));
        await Assemble(store, "m2");

        var m2 = await Load<Commit>(store, Commit.DocumentId(RepoId, "m2"));
        var expected = 5 * 100d / 6 - 4 * 100d / 6;
        Assert.NotNull(m2!.CoverageDeltaVsParent);
        Math.Abs(m2.CoverageDeltaVsParent!.Value - expected).Should().BeLessThan(0.0001);
        Assert.NotNull(m2.CoverageDeltaVsDefaultBranch);
        Math.Abs(m2.CoverageDeltaVsDefaultBranch!.Value - expected).Should().BeLessThan(0.0001);

        // A PR commit off m2 that measures a.cs back to 1/2 (4/6): −16.67 against both parent and master.
        await SeedCommit(store, "p6", "feature", T0.AddHours(2), parentSha: "m2");
        await Upload(store, "p6", runId: 3, LcovABase, FileList(("src/a.cs", OidA1), ("src/b.cs", OidB1)), partial: true, baseSha: "m2");
        await Assemble(store, "p6");

        var p6 = await Load<Commit>(store, Commit.DocumentId(RepoId, "p6"));
        Math.Abs(p6!.CoverageDeltaVsParent!.Value + expected).Should().BeLessThan(0.0001);
        Math.Abs(p6.CoverageDeltaVsDefaultBranch!.Value + expected).Should().BeLessThan(0.0001);
    }

    [Fact]
    public async Task The_backfill_job_verifies_parents_and_stamps_deltas_for_legacy_commits_then_goes_quiet()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0); // m1: 4/6, assembled, parent looked up (scripted: none)

        // m2: a legacy commit — coverage copied from a build, no assembly, no parent.
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Commit
            {
                Repository = RepositoryId, Sha = "m2", Branch = "master", AuthoredAt = T0.AddHours(1),
                Coverage = new CoverageSummary { LinesCovered = 5, LinesCoverable = 6, FilesCount = 2 },
            }, Commit.DocumentId(RepoId, "m2"));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        var github = new ScriptedDiffService();
        github.Parents["m2"] = "m1";

        async Task<int> RunJob()
        {
            using var session = store.OpenAsyncSession();
            var services = new ServiceCollection()
                .AddSingleton(store)
                .AddSingleton(session)
                .AddSingleton<IGitHubDiffService>(github)
                .AddSingleton<IBaseResolver, BaseResolver>()
                .AddSingleton<ICommitAssembler, CommitAssembler>()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider();
            var job = ActivatorUtilities.CreateInstance<BackfillCommitDeltasCronJob>(services);
            await job.RunAsync(CancellationToken.None);
            WaitForIndexing(store);
            using var count = store.OpenAsyncSession();
            return await count.Query<CodeCoverage.Indexes.Commits_ByRepository.Result, CodeCoverage.Indexes.Commits_ByRepository>()
                .Where(r => r.HasCoverage && !r.ParentLookupDone)
                .CountAsync();
        }

        (await RunJob()).Should().Be(0);

        var m2 = await Load<Commit>(store, Commit.DocumentId(RepoId, "m2"));
        m2!.ParentSha.Should().Be("m1");
        m2.ParentShaSource.Should().Be("api");
        Assert.NotNull(m2.ParentLookupAttemptedAtUtc);
        var expected = 5 * 100d / 6 - 4 * 100d / 6;
        Assert.NotNull(m2.CoverageDeltaVsParent);
        Math.Abs(m2.CoverageDeltaVsParent!.Value - expected).Should().BeLessThan(0.0001);
        Assert.NotNull(m2.CoverageDeltaVsDefaultBranch);

        // m1 had no parent to find, but was still marked as attempted by its assembly.
        var m1 = await Load<Commit>(store, Commit.DocumentId(RepoId, "m1"));
        Assert.NotNull(m1!.ParentLookupAttemptedAtUtc);
    }

    [Fact]
    public async Task Reassembling_a_parent_restamps_its_children()
    {
        using var store = GetDocumentStore();
        await SeedAssembledBase(store, T0);
        await SeedCommit(store, "m2", "master", T0.AddHours(1), parentSha: "m1");
        await Upload(store, "m2", runId: 2, LcovAHead + LcovB, FileList(("src/a.cs", OidA2), ("src/b.cs", OidB1)));
        await Assemble(store, "m2");

        // A second run on m1 lands late and raises m1 to 5/6 as well → m2's Δ vs parent collapses to 0.
        await Upload(store, "m1", runId: 9, LcovAHead, FileList(("src/a.cs", OidA1), ("src/b.cs", OidB1)));
        await Assemble(store, "m1");

        var m2 = await Load<Commit>(store, Commit.DocumentId(RepoId, "m2"));
        Assert.NotNull(m2!.CoverageDeltaVsParent);
        Math.Abs(m2.CoverageDeltaVsParent!.Value).Should().BeLessThan(0.0001);
    }
}
