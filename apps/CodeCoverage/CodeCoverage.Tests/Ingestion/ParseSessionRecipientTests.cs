using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using CodeCoverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Runs the real recipient against an embedded RavenDB with the client's
/// default 30-requests-per-session budget in force. Regression for the launch
/// bug: per-file LoadAsync calls exhausted the budget ~28 files into the first
/// real upload (hundreds of source files), the failure was then reported
/// through the same exhausted session — so it never persisted either — and
/// every real-world session sat "Pending" with error null forever. Fixture-
/// sized tests can't catch that class of bug; this one parses well past the
/// budget.
/// </summary>
public class ParseSessionRecipientTests : CoverageRavenTest
{
    private const int FileCount = 200;
    private const string BuildId = "Commits/1/abc/builds/1-1";
    private const string SessionId = "s1";

    [Fact]
    public async Task Parses_a_report_with_far_more_files_than_the_session_request_budget()
    {
        using var store = GetDocumentStore();
        await SeedBuildWithLcovReport(store, FileCount);

        using (var session = store.OpenAsyncSession()) // default budget: 30 requests
        {
            var recipient = CreateRecipient(store, session);
            await recipient.HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = SessionId });
        }

        using var assertSession = store.OpenAsyncSession();
        var build = await assertSession.LoadAsync<Build>(BuildId);
        var buildSession = build.Sessions.Single();

        buildSession.ParseStatus.Should().Be("Parsed");
        buildSession.Error.Should().BeNull();
        buildSession.FilesCount.Should().Be(FileCount);
        build.Coverage.Should().NotBeNull();

        var fileCoverages = await assertSession.Advanced
            .LoadStartingWithAsync<FileCoverage>($"{BuildId}/files/", pageSize: 1024);
        fileCoverages.Should().HaveCount(FileCount);
        fileCoverages.Should().OnlyContain(f => f.Matched);
    }

    [Fact]
    public async Task A_parse_failure_is_persisted_even_when_the_scoped_session_is_poisoned()
    {
        using var store = GetDocumentStore();
        await SeedBuildWithLcovReport(store, fileCount: 3);

        using (var session = store.OpenAsyncSession())
        {
            // Make every SaveChangesAsync on the scoped session throw — the
            // shape of the launch failure, where the session was already
            // unusable by the time the catch block tried to report through
            // it, so the "Failed" status never persisted and the session sat
            // "Pending" with error null forever.
            session.Advanced.OnBeforeStore += (_, _) => throw new InvalidOperationException("injected session fault");
            var recipient = CreateRecipient(store, session);
            await recipient.HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = SessionId });
        }

        using var assertSession = store.OpenAsyncSession();
        var build = await assertSession.LoadAsync<Build>(BuildId);
        var buildSession = build.Sessions.Single();

        buildSession.ParseStatus.Should().Be("Failed");
        buildSession.Error.Should().Contain("injected session fault");
    }

    [Fact]
    public async Task A_v2_file_list_stamps_blob_oids_on_matched_files()
    {
        using var store = GetDocumentStore();
        await SeedBuildWithLcovReport(store, fileCount: 3, withOids: true);

        using (var session = store.OpenAsyncSession())
        {
            var recipient = CreateRecipient(store, session);
            await recipient.HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = SessionId });
        }

        using var assertSession = store.OpenAsyncSession();
        var fileCoverages = await assertSession.Advanced
            .LoadStartingWithAsync<FileCoverage>($"{BuildId}/files/", pageSize: 1024);

        fileCoverages.Should().HaveCount(3);
        fileCoverages.Should().OnlyContain(f => f.Matched && f.BlobOid == OidFor(f.Path));
    }

    [Fact]
    public async Task A_v1_file_list_leaves_blob_oids_null()
    {
        using var store = GetDocumentStore();
        await SeedBuildWithLcovReport(store, fileCount: 3, withOids: false);

        using (var session = store.OpenAsyncSession())
        {
            var recipient = CreateRecipient(store, session);
            await recipient.HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = SessionId });
        }

        using var assertSession = store.OpenAsyncSession();
        var fileCoverages = await assertSession.Advanced
            .LoadStartingWithAsync<FileCoverage>($"{BuildId}/files/", pageSize: 1024);

        fileCoverages.Should().HaveCount(3);
        fileCoverages.Should().OnlyContain(f => f.Matched && f.BlobOid == null);
    }

    [Fact]
    public async Task A_session_with_no_report_is_parsed_not_failed()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            var build = new Build
            {
                Commit = "Commits/1/abc", CiRunId = 1, CiRunAttempt = 1, CreatedAtUtc = DateTime.UtcNow, Partial = true,
                Sessions = [new BuildSession { SessionId = SessionId, RootDir = "/w", RawFileNames = [] }],
            };
            await seed.StoreAsync(build, BuildId);
            seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(SessionId),
                new MemoryStream(Encoding.UTF8.GetBytes($"{OidFor("src/a.cs")} src/a.cs")));
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
        {
            await CreateRecipient(store, session).HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = SessionId });
        }

        using var assertSession = store.OpenAsyncSession();
        var buildSession = (await assertSession.LoadAsync<Build>(BuildId)).Sessions.Single();
        buildSession.ParseStatus.Should().Be("Parsed");
        buildSession.Error.Should().BeNull();
        buildSession.FilesCount.Should().Be(0);
    }

    /// <summary>Deterministic fake OID per path: 40 hex chars derived from the path's hash.</summary>
    private static string OidFor(string path)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path)));

    private static ParseSessionRecipient CreateRecipient(IDocumentStore store, Raven.Client.Documents.Session.IAsyncDocumentSession session)
    {
        // ActivatorUtilities resolves the generated constructor's parameters
        // by type, so this doesn't depend on the [Inject] field order.
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(session)
            .AddSingleton<ICoverageParserFactory, CoverageParserFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ParseSessionRecipient>(services);
    }

    private static async Task SeedBuildWithLcovReport(IDocumentStore store, int fileCount, bool withOids = false)
    {
        var reportName = UploadAttachments.ReportName(SessionId, 0, "lcov.info");
        var paths = Enumerable.Range(0, fileCount).Select(i => $"libs/demo/src/file{i}.ts").ToArray();

        using var seed = store.OpenAsyncSession();
        var build = new Build
        {
            Commit = "Commits/1/abc",
            CiRunId = 1,
            CiRunAttempt = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Sessions =
            [
                new BuildSession
                {
                    SessionId = SessionId,
                    RootDir = "/home/runner/work/repo/repo",
                    RawFileNames = [reportName],
                },
            ],
        };
        await seed.StoreAsync(build, BuildId);

        seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(SessionId),
            new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', paths.Select(p => withOids ? $"{OidFor(p)} {p}" : p)))));
        seed.Advanced.Attachments.Store(build, reportName,
            new MemoryStream(Encoding.UTF8.GetBytes(GenerateLcov(paths))));
        await seed.SaveChangesAsync();
    }

    private static string GenerateLcov(string[] paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths)
        {
            sb.Append("SF:/home/runner/work/repo/repo/").Append(path).Append('\n');
            sb.Append("DA:1,3\n");
            sb.Append("DA:2,0\n");
            sb.Append("LF:2\nLH:1\n");
            sb.Append("end_of_record\n");
        }
        return sb.ToString();
    }
}
