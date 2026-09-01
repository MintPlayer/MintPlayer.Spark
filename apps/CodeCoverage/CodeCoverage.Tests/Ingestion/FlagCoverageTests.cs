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
/// Flags end-to-end: parse merges each flagged session's files into per-flag
/// documents (the only moment attribution exists — the build-level merge
/// destroys it), finalize materializes per-flag trees and totals, retries
/// stay idempotent because per-flag merging is the same max-merge.
/// </summary>
public class FlagCoverageTests : CoverageRavenTest
{
    private const string BuildId = "Commits/7/def/builds/9-1";

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

    /// <summary>Two sessions with different flags covering different files.</summary>
    private static async Task Seed(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        var build = new Build
        {
            Commit = "Commits/7/def",
            CiRunId = 9,
            CiRunAttempt = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Sessions =
            [
                new BuildSession { SessionId = "s1", Flags = ["unit"], RootDir = "/w", RawFileNames = [UploadAttachments.ReportName("s1", 0, "lcov.info")] },
                new BuildSession { SessionId = "s2", Flags = ["e2e", "Linux"], RootDir = "/w", RawFileNames = [UploadAttachments.ReportName("s2", 0, "lcov.info")] },
            ],
        };
        await seed.StoreAsync(build, BuildId);
        seed.Advanced.Attachments.Store(build, UploadAttachments.ReportName("s1", 0, "lcov.info"),
            new MemoryStream(Encoding.UTF8.GetBytes("SF:/w/libs/a/x.ts\nDA:1,1\nDA:2,0\nend_of_record\n")));
        seed.Advanced.Attachments.Store(build, UploadAttachments.ReportName("s2", 0, "lcov.info"),
            new MemoryStream(Encoding.UTF8.GetBytes("SF:/w/libs/b/y.ts\nDA:1,1\nDA:2,1\nend_of_record\n")));
        seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName("s1"),
            new MemoryStream(Encoding.UTF8.GetBytes("libs/a/x.ts\nlibs/b/y.ts")));
        await seed.SaveChangesAsync();
    }

    private static async Task ParseBoth(IDocumentStore store)
    {
        foreach (var sessionId in new[] { "s1", "s2" })
        {
            using var session = store.OpenAsyncSession();
            var recipient = CreateRecipient(store, session);
            await recipient.HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = sessionId });
        }
    }

    [Fact]
    public async Task Flagged_sessions_produce_per_flag_documents_and_finalize_totals()
    {
        using var store = GetDocumentStore();
        await Seed(store);
        await ParseBoth(store);

        using (var session = store.OpenAsyncSession())
        {
            var build = await session.LoadAsync<Build>(BuildId);
            await BuildFinalizer.Finalize(session, new ScriptedDiffService(), build, "Explicit", CancellationToken.None);
            await session.SaveChangesAsync();
        }

        using var verify = store.OpenAsyncSession();
        var finalized = await verify.LoadAsync<Build>(BuildId);

        finalized.FlagCoverage.Should().NotBeNull();
        finalized.FlagCoverage!.Keys.Should().BeEquivalentTo(["unit", "e2e", "linux"]);

        // unit saw only x.ts (1 of 2); e2e/linux only y.ts (2 of 2).
        finalized.FlagCoverage["unit"].LinesCoverable.Should().Be(2);
        finalized.FlagCoverage["unit"].LinesCovered.Should().Be(1);
        finalized.FlagCoverage["e2e"].LinesCovered.Should().Be(2);
        finalized.FlagCoverage["linux"].FilesCount.Should().Be(1);

        // The build-level totals span both files — flags never narrow them.
        var buildTree = await verify.LoadAsync<BuildTreeSummary>(BuildTreeSummary.DocumentId(BuildId));
        buildTree!.Files.Should().HaveCount(2);

        var unitTree = await verify.LoadAsync<BuildTreeSummary>(BuildTreeSummary.FlagDocumentId(BuildId, "unit"));
        unitTree!.Files.Should().ContainSingle(f => f.Path == "libs/a/x.ts");

        // Session file counts count files, not per-flag copies.
        finalized.Sessions.Single(s => s.SessionId == "s1").FilesCount.Should().Be(1);
    }

    [Fact]
    public async Task Reparsing_the_same_session_is_idempotent_for_flag_totals()
    {
        using var store = GetDocumentStore();
        await Seed(store);
        await ParseBoth(store);
        await ParseBoth(store); // retry — max-merge must not double anything

        using (var session = store.OpenAsyncSession())
        {
            var build = await session.LoadAsync<Build>(BuildId);
            await BuildFinalizer.Finalize(session, new ScriptedDiffService(), build, "Explicit", CancellationToken.None);
            await session.SaveChangesAsync();
        }

        using var verify = store.OpenAsyncSession();
        var finalized = await verify.LoadAsync<Build>(BuildId);
        finalized.FlagCoverage!["unit"].LinesCoverable.Should().Be(2);
        finalized.FlagCoverage["unit"].LinesCovered.Should().Be(1);
    }
}
