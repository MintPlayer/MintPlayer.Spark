using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Two CI runs (distinct <c>runId</c>) for the same commit each upload a report
/// covering a disjoint file. Each run is its own <see cref="Build"/>, so the
/// per-build session merge never sees the other run's files, and
/// <see cref="BuildFinalizer"/> promotes <c>build.Coverage</c> onto the Commit
/// wholesale — whichever build finalizes last wins.
///
/// The commit's coverage should be the union of every build for that sha,
/// independent of finalize order. These tests assert that union; today they
/// are red because the commit carries only the last-finalized build's totals.
/// M2 (CommitAssembly) makes them green by removing the Skip.
/// </summary>
public class CrossRunAssemblyTests : CoverageRavenTest
{
    private const string SkipReason = "Red until M2 (CommitAssembly) — see docs/coverage_carryforward_plan.md";

    private const long RepoId = 7;
    private const string Sha = "abc";
    private static readonly string CommitId = Commit.DocumentId(RepoId, Sha);
    private static readonly string Build1 = Build.DocumentId(RepoId, Sha, runId: 1, runAttempt: 1);
    private static readonly string Build2 = Build.DocumentId(RepoId, Sha, runId: 2, runAttempt: 1);

    // Report A: src/a.cs, 3 coverable lines, 2 covered.
    private const string ReportA = "SF:/w/src/a.cs\nDA:1,1\nDA:2,1\nDA:3,0\nend_of_record\n";
    // Report B: src/b.cs, 2 coverable lines, 1 covered.
    private const string ReportB = "SF:/w/src/b.cs\nDA:1,1\nDA:2,0\nend_of_record\n";

    private static ParseSessionRecipient CreateRecipient(IDocumentStore store, Raven.Client.Documents.Session.IAsyncDocumentSession session)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(session)
            .AddSingleton<ICoverageParserFactory, CoverageParserFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ParseSessionRecipient>(services);
    }

    /// <summary>One Commit, two Builds (runId 1 and 2), one lcov session each.</summary>
    private static async Task Seed(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Commit { Sha = Sha, FirstSeenAtUtc = DateTimeOffset.UtcNow }, CommitId);
        await SeedBuild(seed, Build1, ciRunId: 1, "s1", ReportA, "src/a.cs");
        await SeedBuild(seed, Build2, ciRunId: 2, "s2", ReportB, "src/b.cs");
        await seed.SaveChangesAsync();
    }

    private static async Task SeedBuild(Raven.Client.Documents.Session.IAsyncDocumentSession seed, string buildId, long ciRunId, string sessionId, string lcov, string fileList)
    {
        var reportName = UploadAttachments.ReportName(sessionId, 0, "lcov.info");
        var build = new Build
        {
            Commit = CommitId,
            CiRunId = ciRunId,
            CiRunAttempt = 1,
            Run = Build.ComposeRun(ciRunId, 1),
            CreatedAtUtc = DateTime.UtcNow,
            Sessions = [new BuildSession { SessionId = sessionId, RootDir = "/w", RawFileNames = [reportName] }],
        };
        await seed.StoreAsync(build, buildId);
        seed.Advanced.Attachments.Store(build, reportName, new MemoryStream(Encoding.UTF8.GetBytes(lcov)));
        seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId), new MemoryStream(Encoding.UTF8.GetBytes(fileList)));
    }

    private static async Task Parse(IDocumentStore store, string buildId, string sessionId)
    {
        using var session = store.OpenAsyncSession();
        var recipient = CreateRecipient(store, session);
        await recipient.HandleAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId });
    }

    private static async Task Finalize(IDocumentStore store, string buildId)
    {
        using var session = store.OpenAsyncSession();
        var build = await session.LoadAsync<Build>(buildId);
        await BuildFinalizer.Finalize(session, new ScriptedDiffService(), build, "Explicit", CancellationToken.None);
        await session.SaveChangesAsync();
    }

    private static async Task<Commit> LoadCommit(IDocumentStore store)
    {
        using var verify = store.OpenAsyncSession();
        return await verify.LoadAsync<Commit>(CommitId);
    }

    /// <summary>A ∪ B: 2 files, 5 coverable lines, 3 covered — in either order.</summary>
    private static void ShouldBeUnionOfBothRuns(Commit commit)
    {
        commit.Coverage.Should().NotBeNull();
        commit.Coverage!.LinesCoverable.Should().Be(5);
        commit.Coverage.LinesCovered.Should().Be(3);
        commit.Coverage.FilesCount.Should().Be(2);
    }

    [Fact(Skip = SkipReason)]
    public async Task Finalizing_run_1_then_run_2_yields_the_union_on_the_commit()
    {
        using var store = GetDocumentStore();
        await Seed(store);
        await Parse(store, Build1, "s1");
        await Parse(store, Build2, "s2");

        await Finalize(store, Build1);
        await Finalize(store, Build2);

        // Today: equals B alone (FilesCount 1, LinesCoverable 2, LinesCovered 1).
        ShouldBeUnionOfBothRuns(await LoadCommit(store));
    }

    [Fact(Skip = SkipReason)]
    public async Task Finalizing_run_2_then_run_1_yields_the_union_on_the_commit()
    {
        using var store = GetDocumentStore();
        await Seed(store);
        await Parse(store, Build1, "s1");
        await Parse(store, Build2, "s2");

        await Finalize(store, Build2);
        await Finalize(store, Build1);

        // Today: equals A alone (FilesCount 1, LinesCoverable 3, LinesCovered 2).
        ShouldBeUnionOfBothRuns(await LoadCommit(store));
    }
}
