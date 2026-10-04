using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Operations.Attachments;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The two parts of the forge re-key that <see cref="ForgeQualifiedDocumentIdsMigrationTests"/>
/// leaves out: moving the uploaded reports onto the re-keyed builds, and the guard that refuses to
/// delete when the re-key produced nothing.
/// </summary>
/// <remarks>
/// Attachments cannot be moved by a patch script — their names are only in the document's
/// metadata — so this phase is a client-side loop, and a build whose reports did not follow would
/// lose them when phase four deletes the legacy copy. The guard is the migration's safety net: its
/// failure mode is destroying the only copy of a collection.
/// </remarks>
public class ForgeQualifiedMigrationGuardTests : CoverageRavenTest
{
    private const long RepositoryId = 51_000;
    private const string Sha = "5151515151515151515151515151515151515151";
    private const string ReportName = "sessions/s1/0/lcov.info";

    private static string LegacyBuildId => $"Commits/{RepositoryId}/{Sha}/builds/1-1";
    private static string QualifiedBuildId => $"Commits/github/{RepositoryId}/{Sha}/builds/1-1";

    private static Task RunAsync(IDocumentStore store)
        => new M_202609210900_ForgeQualifiedDocumentIds(store, NullLogger<M_202609210900_ForgeQualifiedDocumentIds>.Instance)
            .UpAsync(CancellationToken.None);

    private static async Task<string?> AttachmentTextAsync(IDocumentStore store, string documentId)
    {
        using var attachment = await store.Operations.SendAsync(new GetAttachmentOperation(documentId, ReportName, AttachmentType.Document, null));
        if (attachment is null) return null;
        using var reader = new StreamReader(attachment.Stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task SeedLegacyBuildAsync(IDocumentStore store, string report)
    {
        using var seed = store.OpenAsyncSession();
        var build = new Build { Commit = $"Commits/{RepositoryId}/{Sha}", CiRunId = 1, CiRunAttempt = 1 };
        await seed.StoreAsync(build, LegacyBuildId);
        seed.Advanced.Attachments.Store(build, ReportName, new MemoryStream(Encoding.UTF8.GetBytes(report)), "text/plain");
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task A_legacy_builds_reports_follow_it_onto_the_re_keyed_id()
    {
        using var store = GetDocumentStore();
        await SeedLegacyBuildAsync(store, "TN:\nSF:a.ts\nend_of_record\n");

        await RunAsync(store);

        (await AttachmentTextAsync(store, QualifiedBuildId)).Should().Be("TN:\nSF:a.ts\nend_of_record\n");
        using var session = store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(LegacyBuildId)).Should().BeFalse("the legacy copy is deleted once its reports have moved");
    }

    /// <summary>
    /// An interrupted run left the report already on the new id; the re-run neither copies it again
    /// nor overwrites it with the legacy bytes.
    /// </summary>
    [Fact]
    public async Task A_report_already_moved_by_an_interrupted_run_is_left_as_it_is()
    {
        using var store = GetDocumentStore();
        await SeedLegacyBuildAsync(store, "legacy bytes");
        using (var seed = store.OpenAsyncSession())
        {
            var moved = new Build { Commit = $"Commits/github/{RepositoryId}/{Sha}", CiRunId = 1, CiRunAttempt = 1 };
            await seed.StoreAsync(moved, QualifiedBuildId);
            seed.Advanced.Attachments.Store(moved, ReportName, new MemoryStream(Encoding.UTF8.GetBytes("already moved")), "text/plain");
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        (await AttachmentTextAsync(store, QualifiedBuildId)).Should().Be("already moved");
    }

    /// <summary>
    /// The guard, provoked by the failure it describes: a document the put script does not match —
    /// here an id whose casing the script's prefix test does not recognise — so the re-key produces
    /// nothing for that collection. Deleting would then destroy the only copy; the migration throws
    /// instead, and the legacy document survives.
    /// </summary>
    [Fact]
    public async Task The_migration_refuses_to_delete_a_collection_the_re_key_did_not_copy()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 9, Login = "cased" }, "accounts/9");
            await seed.SaveChangesAsync();
        }

        var ex = (await new Func<Task>(() => RunAsync(store)).Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        ex.Message.Should().Contain("Refusing to delete 1 legacy 'Accounts' documents");
        using var session = store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync("accounts/9")).Should().BeTrue("the only copy must survive the refusal");
    }
}
