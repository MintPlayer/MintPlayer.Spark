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
            var headFileList = HeadFileList.Parse(
                await ReadAttachmentText(build, UploadAttachments.FileListName(message.SessionId), cancellationToken));

            var touched = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
            var parsedAnything = false;

            foreach (var attachmentName in buildSession.RawFileNames)
            {
                var content = await ReadAttachmentText(build, attachmentName, cancellationToken);
                if (content is null)
                {
                    logger.LogWarning("Attachment {Name} missing on {BuildId}", attachmentName, message.BuildId);
                    continue;
                }

                var parser = parserFactory.Resolve(content);
                if (parser is null)
                {
                    logger.LogWarning("Unrecognized report format in {Name} on {BuildId}", attachmentName, message.BuildId);
                    continue;
                }

                var result = parser.Parse(content);
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
                            fileCoverage = new FileCoverage { BuildId = build.Id!, Path = path, Matched = matched };
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
            buildSession.ParseStatus = parsedAnything || nothingToParse ? "Parsed" : "Failed";
            buildSession.Error = parsedAnything || nothingToParse ? null : "No parsable coverage report found in the upload";
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

    private async Task<string?> ReadAttachmentText(Build build, string name, CancellationToken cancellationToken)
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
            await gzip.CopyToAsync(decompressed, cancellationToken);
            bytes = decompressed.ToArray();
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
