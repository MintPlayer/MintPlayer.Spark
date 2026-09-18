using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion.Parsing;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Parses one uploaded session's raw report attachments and merges the result
/// into the Build's FileCoverage documents (max semantics). Sessions of a queue
/// are processed strictly FIFO (Spark messaging, MaxDocsPerBatch=1), so the
/// read-modify-write on FileCoverage needs no extra locking.
/// </summary>
public partial class ParseSessionRecipient : IRecipient<ParseSessionMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ICoverageParserFactory parserFactory;
    [Inject] private readonly ILogger<ParseSessionRecipient> logger;

    public async Task HandleAsync(ParseSessionMessage message, CancellationToken cancellationToken = default)
    {
        // Attachments alone cost one request per uploaded report, so a
        // many-report upload can pass Raven's default 30-request budget even
        // with the batched FileCoverage loads below.
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var build = await session.LoadAsync<Build>(message.BuildId, cancellationToken);
        var buildSession = build?.Sessions.FirstOrDefault(s => s.SessionId == message.SessionId);
        if (build is null || buildSession is null)
        {
            logger.LogWarning("Build {BuildId} / session {SessionId} not found — skipping parse", message.BuildId, message.SessionId);
            return;
        }

        try
        {
            var fileListBytes = await ReadAttachmentBytes(build, UploadAttachments.FileListName(message.SessionId), cancellationToken);
            var headFileList = HeadFileList.Parse(
                fileListBytes is null ? null : ReportContent.FromBytes(fileListBytes).Text);

            // Row E of the #415 table: with BOTH the workspace root and the file list
            // absent, PathNormalizer can only fall back to "is it still absolute?", so
            // every absolute path resolves unmatched and the build measures nothing.
            // That combination used to be invisible — the build simply came out empty.
            // Say it out loud, at the point it is known.
            if (headFileList.Paths.Count == 0 && string.IsNullOrWhiteSpace(buildSession.RootDir))
            {
                logger.LogWarning(
                    "Session {SessionId} of {BuildId} has neither a workspace root nor a file list — "
                    + "absolute report paths cannot be resolved and will be reported unmatched.",
                    message.SessionId, message.BuildId);
            }

            var touched = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
            var parsedAnything = false;
            var outcomes = new List<ReportIngestOutcome>();

            foreach (var attachmentName in buildSession.RawFileNames)
            {
                // Per-file isolation (#417). Before this, a parser throw escaped the
                // loop and failed the whole session — so one malformed report
                // discarded every report that had already merged, because
                // SaveChangesAsync below was never reached.
                var outcome = new ReportIngestOutcome { FileName = UploadAttachments.DisplayName(attachmentName) };
                outcomes.Add(outcome);

                ReportContent content;
                ICoverageParser parser;
                ParseResult result;
                try
                {
                    var bytes = await ReadAttachmentBytes(build, attachmentName, cancellationToken);
                    if (bytes is null)
                    {
                        Reject(outcome, ReportRejectionReason.Missing, "The upload's attachment was not found on the build.");
                        logger.LogWarning("Attachment {Name} missing on {BuildId}", attachmentName, message.BuildId);
                        continue;
                    }

                    content = ReportContent.FromBytes(bytes);
                    if (content.IsEmpty)
                    {
                        Reject(outcome, ReportRejectionReason.Empty,
                            bytes.Length == 0 ? "The file is empty (0 bytes)." : "The file contains no content once decoded.");
                        logger.LogWarning("Empty report {Name} on {BuildId}", attachmentName, message.BuildId);
                        continue;
                    }

                    var resolved = parserFactory.Resolve(content);
                    if (resolved is null)
                    {
                        Reject(outcome, ReportRejectionReason.UnrecognizedFormat,
                            "No supported parser recognised this file. Supported: Cobertura, JaCoCo and LCOV.");
                        logger.LogWarning("Unrecognized report format in {Name} on {BuildId}", attachmentName, message.BuildId);
                        continue;
                    }

                    parser = resolved;
                    outcome.Format = parser.FormatName;
                    result = parser.Parse(content);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Reject(outcome, ClassifyParseFailure(ex), ex.Message);
                    logger.LogWarning(ex, "Rejected report {Name} on {BuildId}", attachmentName, message.BuildId);
                    continue;
                }

                if (result.Files.Count == 0)
                {
                    Reject(outcome, ReportRejectionReason.NoFiles,
                        $"Parsed as {parser.FormatName}, but the report described no files.");
                    logger.LogWarning("Report {Name} on {BuildId} parsed as {Format} with no files",
                        attachmentName, message.BuildId, parser.FormatName);
                    continue;
                }

                outcome.Parsed = true;
                outcome.FilesCount = result.Files.Count;

                var normalizer = new PathNormalizer(buildSession.RootDir, result.SourceRoots, headFileList.Paths);

                // Each parsed file merges into the build-level document AND
                // one per-flag document per session flag — the only point
                // where flag attribution still exists; the merged build-level
                // documents cannot recover it later (max destroys provenance).
                var normalized = result.Files
                    .Select(parsedFile =>
                    {
                        var (path, matched) = normalizer.Normalize(parsedFile.RawPath);
                        string[] documentIds =
                        [
                            FileCoverage.DocumentId(build.Id!, path),
                            .. buildSession.Flags.Select(flag => FileCoverage.FlagDocumentId(build.Id!, flag, path)),
                        ];
                        return (parsedFile, path, matched, documentIds);
                    })
                    .ToList();

                // One round-trip per 512 unseen ids instead of one per file:
                // a real upload references hundreds of source files, and
                // per-file loads exhausted the session's request budget ~28
                // files in — every real-world session parsed zero files.
                var unseenIds = normalized
                    .SelectMany(f => f.documentIds)
                    .Where(id => !touched.ContainsKey(id))
                    .Distinct(StringComparer.Ordinal);
                foreach (var chunk in unseenIds.Chunk(512))
                {
                    var loaded = await session.LoadAsync<FileCoverage>(chunk, cancellationToken);
                    foreach (var (id, existing) in loaded)
                    {
                        if (existing is not null)
                            touched[id] = existing;
                    }
                }

                foreach (var (parsedFile, path, matched, documentIds) in normalized)
                {
                    foreach (var documentId in documentIds)
                    {
                        if (!touched.TryGetValue(documentId, out var fileCoverage))
                        {
                            fileCoverage = new FileCoverage
                            {
                                BuildId = build.Id!,
                                Path = path,
                                Matched = matched,
                                // Only when it differs — storing a copy of Path for every
                                // file would double the field for no diagnostic value.
                                RawPath = string.Equals(parsedFile.RawPath, path, StringComparison.Ordinal)
                                    ? null
                                    : parsedFile.RawPath,
                            };
                            await session.StoreAsync(fileCoverage, documentId, cancellationToken);
                            touched[documentId] = fileCoverage;
                        }

                        fileCoverage.Matched |= matched;
                        if (matched)
                            fileCoverage.BlobOid ??= headFileList.OidFor(path);
                        CoverageMerger.MergeInto(fileCoverage, parsedFile, parser.FormatName);
                    }
                }

                parsedAnything = true;
                logger.LogInformation("Parsed {Name} ({Format}, {Files} files) for {BuildId}",
                    attachmentName, parser.FormatName, result.Files.Count, message.BuildId);
            }

            // A session that deliberately carried no report (zero-report partial
            // upload) has nothing to fail at; the assembler fills the commit in.
            var nothingToParse = buildSession.RawFileNames.Length == 0;
            buildSession.Reports = outcomes;
            buildSession.ParseStatus = parsedAnything || nothingToParse ? "Parsed" : "Failed";
            // A session fails only when EVERY report was rejected (#417). When some
            // parsed and some did not, the session is Parsed and the per-report
            // outcomes carry the rejections — the build still reports errors, because
            // ClassifyState reads the outcomes too.
            buildSession.Error = parsedAnything || nothingToParse ? null : DescribeRejections(outcomes);
            // Build-level documents only — the per-flag copies are the same
            // files again, not more files.
            buildSession.FilesCount = touched.Keys.Count(id => !id.Contains("/flags/", StringComparison.Ordinal));

            // Persist the merged FileCoverage docs first — the summary streams
            // them back from the server (it must also cover files earlier
            // sessions touched but this one didn't).
            await session.SaveChangesAsync(cancellationToken);
            await RecomputeBuildSummary(build, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse session {SessionId} of {BuildId}", message.SessionId, message.BuildId);
            await ReportParseFailure(message, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Reports through a FRESH session: the scoped one may be the very thing
    /// that failed (exhausted request budget, faulted connection), and
    /// reporting through it used to throw again — leaving the session
    /// "Pending" with error null, a status that reads like "the worker never
    /// ran" instead of carrying the diagnosis.
    /// </summary>
    private async Task ReportParseFailure(ParseSessionMessage message, Exception ex, CancellationToken cancellationToken)
    {
        using var freshSession = documentStore.OpenAsyncSession();
        var build = await freshSession.LoadAsync<Build>(message.BuildId, cancellationToken);
        var buildSession = build?.Sessions.FirstOrDefault(s => s.SessionId == message.SessionId);
        if (buildSession is null)
            return;

        buildSession.ParseStatus = "Failed";
        buildSession.Error = ex.Message;
        await freshSession.SaveChangesAsync(cancellationToken);
    }

    private async Task RecomputeBuildSummary(Build build, CancellationToken cancellationToken)
    {
        var files = new List<FileCoverage>();
        await using (var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{build.Id}/files/", token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                files.Add(stream.Current.Document);
            }
        }

        build.Coverage = CoverageMerger.Summarize(files.Where(f => f.Matched));
    }

    /// <summary>
    /// Upper bound on the DECOMPRESSED size of one report. The controller's
    /// <c>MaxReportBytes</c> bounds the compressed multipart body only, so without
    /// this a small upload could expand without limit (#417). 512 MB is far above any
    /// real report — a monorepo's Cobertura is tens of megabytes — and exists to stop
    /// a zip bomb rather than to express an expectation.
    /// </summary>
    private const long MaxDecompressedBytes = 512L * 1024 * 1024;

    private static void Reject(ReportIngestOutcome outcome, string reason, string detail)
    {
        outcome.Parsed = false;
        outcome.Reason = reason;
        // Bounded: a parser message is usually one line, but it is attacker-influenced
        // text that ends up in a workflow log and a document.
        outcome.Detail = detail.Length > 500 ? detail[..500] + "…" : detail;
    }

    /// <summary>
    /// Separates "the file stops in the middle" from "the file is not well-formed",
    /// because they mean different things to whoever has to fix it: a truncated
    /// report is a CI job killed mid-write, a malformed one is a bad producer.
    /// </summary>
    private static string ClassifyParseFailure(Exception ex) => ex switch
    {
        System.Xml.XmlException xml when xml.Message.Contains("Unexpected end of file", StringComparison.Ordinal)
            => ReportRejectionReason.Truncated,
        System.Xml.XmlException xml when xml.Message.Contains("exceeds the MaxCharacters", StringComparison.OrdinalIgnoreCase)
            => ReportRejectionReason.TooLarge,
        ReportTooLargeException => ReportRejectionReason.TooLarge,
        System.IO.InvalidDataException => ReportRejectionReason.Malformed,
        _ => ReportRejectionReason.Malformed,
    };

    private static string DescribeRejections(List<ReportIngestOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            return "No parsable coverage report found in the upload";

        var detail = string.Join("; ", outcomes.Select(o => $"{o.FileName}: {o.Reason} ({o.Detail})"));
        var message = $"No parsable coverage report found in the upload — {detail}";
        return message.Length > 2000 ? message[..2000] + "…" : message;
    }

    private async Task<byte[]?> ReadAttachmentBytes(Build build, string name, CancellationToken cancellationToken)
    {
        var attachment = await session.Advanced.Attachments.GetAsync(build, name, cancellationToken);
        if (attachment is null) return null;

        await using var stream = attachment.Stream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        // Uploads may be gzipped (magic 1f 8b) — the action gzips by default.
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            await CopyBounded(gzip, decompressed, MaxDecompressedBytes, cancellationToken);
            bytes = decompressed.ToArray();
        }

        return bytes;
    }

    /// <summary>
    /// Copies with a hard ceiling, so decompression cannot outrun memory. Throws
    /// rather than truncating: a silently truncated report is exactly the failure
    /// mode #417 exists to remove.
    /// </summary>
    private static async Task CopyBounded(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
                throw new ReportTooLargeException(limit);

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

/// <summary>Raised when a report's decompressed size exceeds the ingest bound.</summary>
public sealed class ReportTooLargeException(long limit)
    : Exception($"The report expands to more than {limit / (1024 * 1024)} MB when decompressed.");
