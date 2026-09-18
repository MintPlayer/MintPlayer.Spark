using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Ingestion.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Issue #417: an upload the server cannot use must never present as a successful
/// build, and one bad report must not discard the good ones.
///
/// Before this, <c>parser.Parse</c> sat inside the attachment loop but outside any
/// try, so a throw escaped to the session-level catch — losing every report that had
/// already merged, because <c>SaveChangesAsync</c> was never reached.
/// </summary>
public class ReportIngestOutcomeTests : CoverageRavenTest
{
    private const string BuildId = "Commits/1/abc/builds/1-1";
    private const string SessionId = "s1";

    private const string GoodLcov = "TN:\nSF:src/a.ts\nDA:1,3\nDA:2,0\nend_of_record\n";
    private const string SecondGoodLcov = "TN:\nSF:src/b.ts\nDA:1,1\nend_of_record\n";

    /// <summary>A cobertura document that stops mid-element — a CI job killed while writing.</summary>
    private const string TruncatedCobertura =
        "<?xml version=\"1.0\"?>\n<coverage line-rate=\"1\"><packages><package name=\"P\"><classes>";

    [Fact]
    public async Task One_truncated_report_does_not_discard_the_others()
    {
        using var store = GetDocumentStore();
        await Seed(store, ("a.info", GoodLcov), ("broken.xml", TruncatedCobertura), ("b.info", SecondGoodLcov));

        await Run(store);

        using var assert = store.OpenAsyncSession();
        var build = await assert.LoadAsync<Build>(BuildId);
        var session = build.Sessions.Single();

        // The two good reports survived — the whole point.
        session.ParseStatus.Should().Be("Parsed");
        var files = await assert.Advanced.LoadStartingWithAsync<FileCoverage>($"{BuildId}/files/", pageSize: 64);
        files.Should().HaveCount(2);

        // And the third is named, with a reason, rather than vanishing.
        session.Reports.Should().HaveCount(3);
        session.Reports.Count(r => r.Parsed).Should().Be(2);

        var rejected = session.Reports.Single(r => !r.Parsed);
        rejected.FileName.Should().Be("broken.xml");
        rejected.Reason.Should().Be(ReportRejectionReason.Truncated);
        rejected.Detail.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// A session that ingested some reports is "Parsed", because those files are real.
    /// The build is still not clean, and saying so is the whole point of #417 — the
    /// upload contract always said a partial parse is absorbed into
    /// CompleteWithErrors rather than becoming a fourth state.
    /// </summary>
    [Fact]
    public async Task A_partially_rejected_session_still_makes_the_build_report_errors()
    {
        using var store = GetDocumentStore();
        await Seed(store, ("a.info", GoodLcov), ("broken.xml", TruncatedCobertura));

        await Run(store);

        using var assert = store.OpenAsyncSession();
        var build = await assert.LoadAsync<Build>(BuildId);
        build.Status = "Finalized";

        Build.ClassifyState(build).Should().Be("CompleteWithErrors");
    }

    [Fact]
    public async Task An_all_rejected_session_fails_and_names_every_reason()
    {
        using var store = GetDocumentStore();
        await Seed(store, ("broken.xml", TruncatedCobertura), ("empty.info", ""));

        await Run(store);

        using var assert = store.OpenAsyncSession();
        var build = await assert.LoadAsync<Build>(BuildId);
        var session = build.Sessions.Single();

        session.ParseStatus.Should().Be("Failed");
        session.Reports.Should().HaveCount(2);
        session.Reports.Should().OnlyContain(r => !r.Parsed);

        // The error carries the detail rather than an anonymous "no parsable report".
        session.Error.Should().Contain("broken.xml");
        session.Error.Should().Contain("empty.info");
    }

    [Theory]
    // A zero-byte file is its own diagnosis, not a parse failure.
    [InlineData("", ReportRejectionReason.Empty)]
    // Clover roots at <coverage> exactly as Cobertura does, so it used to be claimed
    // and mis-parsed. Istanbul JSON is discovered and uploaded by the action too.
    [InlineData("{\"src/a.ts\":{\"path\":\"src/a.ts\"}}", ReportRejectionReason.UnrecognizedFormat)]
    [InlineData("this is not a coverage report at all", ReportRejectionReason.UnrecognizedFormat)]
    // Recognised by its root element, then unparseable.
    [InlineData("<coverage line-rate=\"1\"><packages", ReportRejectionReason.Truncated)]
    public async Task A_rejected_report_names_its_reason(string body, string expectedReason)
    {
        using var store = GetDocumentStore();
        await Seed(store, ("report.xml", body));

        await Run(store);

        using var assert = store.OpenAsyncSession();
        var build = await assert.LoadAsync<Build>(BuildId);
        var outcome = build.Sessions.Single().Reports.Single();

        outcome.Parsed.Should().BeFalse();
        outcome.Reason.Should().Be(expectedReason);
        outcome.FileName.Should().Be("report.xml");
    }

    /// <summary>
    /// A report that parses cleanly and describes nothing is not a success. It used to
    /// set parsedAnything and leave the session Parsed with zero files — the exact
    /// "accepted, finalized, measured nothing, green" shape #417 exists to remove.
    /// </summary>
    [Fact]
    public async Task A_report_that_parses_but_describes_no_files_is_rejected()
    {
        using var store = GetDocumentStore();
        await Seed(store, ("empty-coverage.xml", "<?xml version=\"1.0\"?>\n<coverage line-rate=\"0\"><packages /></coverage>"));

        await Run(store);

        using var assert = store.OpenAsyncSession();
        var build = await assert.LoadAsync<Build>(BuildId);
        var outcome = build.Sessions.Single().Reports.Single();

        outcome.Parsed.Should().BeFalse();
        outcome.Reason.Should().Be(ReportRejectionReason.NoFiles);
        build.Sessions.Single().ParseStatus.Should().Be("Failed");
    }

    /// <summary>
    /// THE #415 END-TO-END REGRESSION, through the real recipient rather than the
    /// parser alone: a BOM-carrying report must ingest with no client-side help.
    /// </summary>
    [Fact]
    public async Task A_bom_carrying_report_ingests_end_to_end()
    {
        using var store = GetDocumentStore();
        await Seed(store, ("coverage.cobertura.xml", "﻿" + """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.5" version="1.9">
              <packages><package name="P"><classes>
                <class name="C" filename="src/a.ts"><lines><line number="1" hits="1" /></lines></class>
              </classes></package></packages>
            </coverage>
            """));

        await Run(store);

        using var assert = store.OpenAsyncSession();
        var build = await assert.LoadAsync<Build>(BuildId);
        var session = build.Sessions.Single();

        session.ParseStatus.Should().Be("Parsed");
        session.Reports.Single().Parsed.Should().BeTrue();
        session.Reports.Single().Format.Should().Be("cobertura");
        build.Coverage!.FilesCount.Should().Be(1);
    }

    private static async Task Run(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(session)
            .AddSingleton<ICoverageParserFactory, CoverageParserFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var recipient = ActivatorUtilities.CreateInstance<ParseSessionRecipient>(services);
        await recipient.HandleAsync(new ParseSessionMessage { BuildId = BuildId, SessionId = SessionId });
    }

    private static async Task Seed(IDocumentStore store, params (string Name, string Body)[] reports)
    {
        using var seed = store.OpenAsyncSession();
        var names = reports.Select((r, i) => UploadAttachments.ReportName(SessionId, i, r.Name)).ToArray();

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
                    RawFileNames = names,
                },
            ],
        };
        await seed.StoreAsync(build, BuildId);

        seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(SessionId),
            new MemoryStream(Encoding.UTF8.GetBytes("src/a.ts\nsrc/b.ts\n")));

        for (var i = 0; i < reports.Length; i++)
        {
            seed.Advanced.Attachments.Store(build, names[i],
                new MemoryStream(Encoding.UTF8.GetBytes(reports[i].Body)));
        }

        await seed.SaveChangesAsync();
    }
}
