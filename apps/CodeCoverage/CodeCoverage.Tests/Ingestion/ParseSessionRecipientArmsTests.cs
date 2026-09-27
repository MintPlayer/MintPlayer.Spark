using System.IO.Compression;
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
/// The parse worker's arms that <see cref="ParseSessionRecipientTests"/> and
/// <see cref="ReportIngestOutcomeTests"/> leave out — most importantly the decompression bound,
/// which is a security control on anonymous fork input.
/// </summary>
/// <remarks>
/// The action gzips by default, so the gzip path is the common one in production and had no test.
/// The bound behind it (<c>CopyBounded</c>, 512 MB) is asserted with a small limit rather than by
/// expanding half a gigabyte in a test run; the classification that turns its exception into
/// <c>tooLarge</c> is asserted beside it.
/// </remarks>
public class ParseSessionRecipientArmsTests : CoverageRavenTest
{
    private const string BuildId = "Commits/github/1/abc/builds/1-1";
    private const string SessionId = "s1";

    private const string Lcov = "TN:\nSF:src/a.ts\nDA:1,3\nDA:2,0\nBRDA:1,0,0,1\nBRDA:1,0,1,0\nend_of_record\n";

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    /// <summary>Seeds one session. A report whose bytes are null is named in the session but never attached.</summary>
    private static async Task SeedAsync(IDocumentStore store, string? rootDir, bool withFileList, params (string Name, byte[]? Bytes)[] reports)
    {
        using var seed = store.OpenAsyncSession();
        var names = reports.Select((r, i) => UploadAttachments.ReportName(SessionId, i, r.Name)).ToArray();
        var build = new Build
        {
            Commit = "Commits/github/1/abc", CiRunId = 1, CiRunAttempt = 1, CreatedAtUtc = DateTime.UtcNow,
            Sessions = [new BuildSession { SessionId = SessionId, RootDir = rootDir, RawFileNames = names }],
        };
        await seed.StoreAsync(build, BuildId);
        if (withFileList)
            seed.Advanced.Attachments.Store(build, UploadAttachments.FileListName(SessionId), new MemoryStream(Encoding.UTF8.GetBytes("src/a.ts\n")));
        for (var i = 0; i < reports.Length; i++)
            if (reports[i].Bytes is { } bytes)
                seed.Advanced.Attachments.Store(build, names[i], new MemoryStream(bytes));
        await seed.SaveChangesAsync();
    }

    private static async Task RunAsync(IDocumentStore store, string buildId = BuildId, string sessionId = SessionId)
    {
        using var session = store.OpenAsyncSession();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(session)
            .AddSingleton<ICoverageParserFactory, CoverageParserFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        await ActivatorUtilities.CreateInstance<ParseSessionRecipient>(services)
            .HandleAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId });
    }

    private static async Task<BuildSession> SessionAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<Build>(BuildId))!.Sessions.Single();
    }

    [Fact]
    public async Task A_gzipped_report_is_decompressed_and_parsed()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "/home/runner/work/repo/repo", withFileList: true, ("lcov.info.gz", Gzip(Lcov)));

        await RunAsync(store);

        var parsed = await SessionAsync(store);
        parsed.ParseStatus.Should().Be("Parsed");
        var report = parsed.Reports.Single();
        report.Parsed.Should().BeTrue();
        report.Format.Should().Be("lcov");
        // lcov names its arms, so the branch line counts as identified rather than count-only.
        report.BranchLinesIdentified.Should().Be(1);
        report.BranchLinesCountOnly.Should().Be(0);
    }

    /// <summary>
    /// A body that claims gzip (the magic bytes) and is not: the decompressor's
    /// <see cref="InvalidDataException"/> is classified Malformed, and the report is rejected on
    /// its own rather than failing the session.
    /// </summary>
    [Fact]
    public async Task A_corrupt_gzip_body_is_rejected_as_malformed()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "/repo", withFileList: true,
            ("bad.gz", [0x1f, 0x8b, 0x08, 0x00, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x01, 0x02]),
            ("good.info", Encoding.UTF8.GetBytes(Lcov)));

        await RunAsync(store);

        var parsed = await SessionAsync(store);
        parsed.ParseStatus.Should().Be("Parsed");
        var rejected = parsed.Reports.Single(r => !r.Parsed);
        rejected.FileName.Should().Be("bad.gz");
        rejected.Reason.Should().Be(ReportRejectionReason.Malformed);
    }

    [Fact]
    public async Task A_report_named_by_the_session_but_never_attached_is_rejected_as_missing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "/repo", withFileList: true, ("lost.info", null));

        await RunAsync(store);

        var parsed = await SessionAsync(store);
        parsed.ParseStatus.Should().Be("Failed");
        parsed.Reports.Single().Reason.Should().Be(ReportRejectionReason.Missing);
        parsed.Error.Should().Contain("lost.info");
    }

    /// <summary>
    /// With neither a workspace root nor a file list, a relative path still resolves; only absolute
    /// ones cannot, and that is warned about rather than silently measuring nothing.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_root_and_no_file_list_still_parses_relative_paths()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, rootDir: null, withFileList: false, ("lcov.info", Encoding.UTF8.GetBytes(Lcov)));

        await RunAsync(store);

        var parsed = await SessionAsync(store);
        parsed.ParseStatus.Should().Be("Parsed");
        parsed.FilesCount.Should().Be(1);
    }

    /// <summary>A branch reading that fell back to counting conditions is flagged, and counts as count-only.</summary>
    [Fact]
    public async Task A_degraded_cobertura_branch_reading_counts_as_count_only()
    {
        using var store = GetDocumentStore();
        const string cobertura = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0" version="1.9" timestamp="0">
              <sources><source>/repo</source></sources>
              <packages><package name="P"><classes>
                <class name="C" filename="src/a.ts"><lines>
                  <line number="5" hits="3" branch="true">
                    <conditions><condition number="70" type="jump" coverage="50%" /></conditions>
                  </line>
                </lines></class>
              </classes></package></packages>
            </coverage>
            """;
        await SeedAsync(store, "/repo", withFileList: true, ("coverage.cobertura.xml", Encoding.UTF8.GetBytes(cobertura)));

        await RunAsync(store);

        var report = (await SessionAsync(store)).Reports.Single();
        report.Parsed.Should().BeTrue();
        report.BranchLinesCountOnly.Should().Be(1);
        report.BranchLinesIdentified.Should().Be(0);
    }

    [Theory]
    [InlineData("Commits/github/1/abc/builds/9-9", SessionId)] // no such build
    [InlineData(BuildId, "no-such-session")]                   // build, but not this session
    public async Task A_message_for_a_build_or_session_that_is_gone_is_skipped(string buildId, string sessionId)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "/repo", withFileList: true, ("lcov.info", Encoding.UTF8.GetBytes(Lcov)));

        await RunAsync(store, buildId, sessionId);

        (await SessionAsync(store)).ParseStatus.Should().Be("Pending", "nothing was parsed");
    }

    // ------------------------------------------------------------------------------------------
    // The decompression bound and the failure classification
    // ------------------------------------------------------------------------------------------

    /// <summary>A stream that yields zeros forever: the shape of a decompression bomb, without the bytes.</summary>
    private sealed class EndlessZeros : Stream
    {
        public long Served { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            Served += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The bound throws rather than truncating, and stops reading just past the limit — it does not
    /// buffer the whole expansion first and measure afterwards.
    /// </summary>
    [Fact]
    public async Task Decompression_past_the_limit_throws_instead_of_truncating()
    {
        var source = new EndlessZeros();
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<ReportTooLargeException>(
            () => ParseSessionRecipient.CopyBounded(source, destination, limit: 3 * 1024 * 1024, CancellationToken.None));

        ex.Message.Should().Be("The report expands to more than 3 MB when decompressed.");
        destination.Length.Should().BeLessThanOrEqualTo(3 * 1024 * 1024);
        source.Served.Should().BeLessThan(4 * 1024 * 1024);
    }

    [Fact]
    public async Task Decompression_within_the_limit_copies_everything()
    {
        using var source = new MemoryStream(new byte[200_000]);
        using var destination = new MemoryStream();

        await ParseSessionRecipient.CopyBounded(source, destination, limit: 200_000, CancellationToken.None);

        destination.Length.Should().Be(200_000);
    }

    public static TheoryData<Exception, string> Failures => new()
    {
        { new ReportTooLargeException(512L * 1024 * 1024), ReportRejectionReason.TooLarge },
        { new System.Xml.XmlException("Unexpected end of file has occurred."), ReportRejectionReason.Truncated },
        { new System.Xml.XmlException("'<' is an unexpected token."), ReportRejectionReason.Malformed },
        { new InvalidDataException("bad gzip"), ReportRejectionReason.Malformed },
        { new FormatException("anything else"), ReportRejectionReason.Malformed },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_parse_failure_is_classified_by_what_the_uploader_can_fix(Exception failure, string expected)
        => ParseSessionRecipient.ClassifyParseFailure(failure).Should().Be(expected);

    /// <summary>
    /// The XML reader's own character limit (<c>SafeXml</c> sets <c>MaxCharactersInDocument</c>)
    /// must classify as too large. Asserted against the exception the reader REALLY throws, not a
    /// hand-written message: the classifier matches on message text, and a message written to
    /// match it would prove nothing.
    /// </summary>
    [Fact]
    public void The_xml_readers_own_size_limit_is_classified_too_large()
    {
        var settings = new System.Xml.XmlReaderSettings { MaxCharactersInDocument = 64 };
        var real = Assert.Throws<System.Xml.XmlException>(() =>
        {
            // Create already reads ahead, so it belongs inside the assertion too.
            using var reader = System.Xml.XmlReader.Create(new StringReader($"<coverage>{new string('x', 1000)}</coverage>"), settings);
            while (reader.Read()) { }
        });

        ParseSessionRecipient.ClassifyParseFailure(real).Should().Be(ReportRejectionReason.TooLarge, real.Message);
    }
}
