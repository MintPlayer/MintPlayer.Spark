using System.Text;
using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The retention sweep that drops uploaded reports once they are inert. What it
/// must never do is drop a filelist — <c>BuildComparer</c> and
/// <c>CommitAssembler</c> still read those when the build is a base.
/// </summary>
public class ReapReportAttachmentsCronJobTests : CoverageRavenTest
{
    private const long RepoId = 204431316;
    private const string Sha = "67262d58656fa932d363bcb3287e60c7542665ea";

    private static ReapReportAttachmentsCronJob Create(IAsyncDocumentSession session, int? retentionDays = null)
    {
        var settings = new Dictionary<string, string?>();
        if (retentionDays is not null)
            settings["Coverage:Retention:ReportAttachmentDays"] = retentionDays.Value.ToString();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ReapReportAttachmentsCronJob(session, configuration, NullLogger<ReapReportAttachmentsCronJob>.Instance);
    }

    private static async Task<string> SeedBuildAsync(
        IDocumentStore store,
        DateTime? finalizedAtUtc,
        string sessionId = "s1",
        string[]? reportNames = null,
        bool withFileList = true,
        DateTime? alreadyReapedAtUtc = null)
    {
        reportNames ??= [UploadAttachments.ReportName(sessionId, 0, "lcov.info")];

        using var seed = store.OpenAsyncSession();
        var build = new Build
        {
            Commit = Commit.DocumentId(RepoId, Sha),
            Status = finalizedAtUtc is null ? "Open" : "Finalized",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
            FinalizedAtUtc = finalizedAtUtc,
            ReportsReapedAtUtc = alreadyReapedAtUtc,
            Sessions = [new BuildSession { SessionId = sessionId, RootDir = "/w", RawFileNames = reportNames }],
        };
        var id = Build.DocumentId(RepoId, Sha, 31694883768, 2);
        await seed.StoreAsync(build, id);

        foreach (var name in reportNames)
            seed.Advanced.Attachments.Store(build, name, new MemoryStream(Encoding.UTF8.GetBytes("TN:\nSF:a.ts\nDA:1,1\nend_of_record")));

        if (withFileList)
            seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId), new MemoryStream(Encoding.UTF8.GetBytes("a.ts")));

        await seed.SaveChangesAsync();
        return id;
    }

    private static async Task<List<string>> AttachmentNamesAsync(IDocumentStore store, string buildId)
    {
        using var session = store.OpenAsyncSession();
        var build = await session.LoadAsync<Build>(buildId);
        return [.. session.Advanced.Attachments.GetNames(build).Select(a => a.Name)];
    }

    [Fact]
    public async Task Reports_older_than_the_window_are_dropped_and_the_filelist_survives()
    {
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(store, finalizedAtUtc: DateTime.UtcNow.AddDays(-30));
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 7).RunAsync(default);

        var names = await AttachmentNamesAsync(store, id);
        names.Should().ContainSingle().Which.Should().Be(UploadAttachments.FileListName("s1"),
            "the filelist is still read when this build serves as a comparison base");
    }

    [Fact]
    public async Task The_build_and_its_coverage_survive_the_sweep()
    {
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(store, finalizedAtUtc: DateTime.UtcNow.AddDays(-30));
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 7).RunAsync(default);

        using var check = store.OpenAsyncSession();
        var build = await check.LoadAsync<Build>(id);
        build.Should().NotBeNull();
        build.Sessions.Should().ContainSingle();
        build.ReportsReapedAtUtc.Should().HaveValue("the marker is what stops the sweep revisiting it");
    }

    [Fact]
    public async Task A_build_inside_the_window_is_left_alone()
    {
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(store, finalizedAtUtc: DateTime.UtcNow.AddDays(-2));
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 7).RunAsync(default);

        (await AttachmentNamesAsync(store, id)).Should().HaveCount(2);

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Build>(id)).ReportsReapedAtUtc.Should().NotHaveValue();
    }

    [Fact]
    public async Task An_open_build_is_never_reaped_however_old()
    {
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(store, finalizedAtUtc: null);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 0).RunAsync(default);

        (await AttachmentNamesAsync(store, id)).Should().HaveCount(2,
            "an unfinalized build may still be parsing its uploads");
    }

    [Fact]
    public async Task A_negative_window_disables_the_sweep()
    {
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(store, finalizedAtUtc: DateTime.UtcNow.AddDays(-365));
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: -1).RunAsync(default);

        (await AttachmentNamesAsync(store, id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_report_the_uploader_called_filelist_is_still_reaped()
    {
        // ReportName sanitises to sessions/{id}/0-filelist, which does not end in
        // "/filelist" — getting this test backwards would silently keep every
        // report from a repo that names its coverage file that way.
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(
            store,
            finalizedAtUtc: DateTime.UtcNow.AddDays(-30),
            reportNames: [UploadAttachments.ReportName("s1", 0, "filelist")]);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 7).RunAsync(default);

        var names = await AttachmentNamesAsync(store, id);
        names.Should().ContainSingle().Which.Should().Be(UploadAttachments.FileListName("s1"));
    }

    [Fact]
    public async Task An_already_reaped_build_is_not_revisited()
    {
        using var store = GetDocumentStore();
        var reapedAt = DateTime.UtcNow.AddDays(-1);
        var id = await SeedBuildAsync(
            store,
            finalizedAtUtc: DateTime.UtcNow.AddDays(-30),
            alreadyReapedAtUtc: reapedAt);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 7).RunAsync(default);

        using var check = store.OpenAsyncSession();
        var build = await check.LoadAsync<Build>(id);
        build.ReportsReapedAtUtc.Should().BeCloseTo(reapedAt, TimeSpan.FromSeconds(1),
            "the marker must not be restamped, or the sweep would churn every run");
    }

    [Fact]
    public async Task A_build_predating_the_marker_field_is_reaped()
    {
        // Every build in production today has no ReportsReapedAtUtc at all. An
        // absent field must satisfy the "not yet reaped" filter, or the sweep
        // would silently do nothing on the entire existing backlog.
        using var store = GetDocumentStore();
        var id = await SeedBuildAsync(store, finalizedAtUtc: DateTime.UtcNow.AddDays(-30));

        using (var strip = store.OpenAsyncSession())
        {
            var operation = await store.Operations.SendAsync(
                new Raven.Client.Documents.Operations.PatchByQueryOperation(
                    new Raven.Client.Documents.Queries.IndexQuery
                    {
                        Query = "from Builds update { delete this.ReportsReapedAtUtc; }",
                    }));
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
        }
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
            await Create(session, retentionDays: 7).RunAsync(default);

        var names = await AttachmentNamesAsync(store, id);
        names.Should().ContainSingle().Which.Should().Be(UploadAttachments.FileListName("s1"));
    }
}
