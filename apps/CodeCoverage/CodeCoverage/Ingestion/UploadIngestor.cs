using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// One upload's worth of already-authorized, already-verified facts.
/// </summary>
/// <remarks>
/// ⚠️ <b>Every value here is trusted by the time it arrives.</b> That is the seam's entire purpose:
/// authorization and provenance are decided by the caller, and the ingestor does not re-check them
/// because it cannot — it has no credential, no route and no <c>User</c>. A caller that fills these
/// from a request body without verifying it has defeated the design, so the two callers are
/// deliberately the only two, and they are separate controllers with separate authentication.
/// </remarks>
/// <param name="ContributedFromFork">
/// Stamped onto the commit, and it changes the document id: fork uploads live under the
/// <c>pr/{n}/</c> shape so they cannot collide with first-party coverage for the same sha.
/// ⚠️ Requires <paramref name="PullRequestNumber"/> — a fork upload with no pull request has no
/// namespace to live in, and the ingestor throws rather than silently writing it as first-party.
/// </param>
public sealed record UploadIngestRequest(
    Repository Repository,
    string CommitSha,
    string? Branch,
    int? PullRequestNumber,
    string? BaseRef,
    string? PrBaseSha,
    string? ParentSha,
    long RunId,
    int RunAttempt,
    string? Workflow,
    string? EventName,
    string? JobName,
    string? Flags,
    string? RootDir,
    string? FileList,
    bool Partial,
    bool? CarryForward,
    string? BaseSha,
    IReadOnlyList<IFormFile> Files,
    bool ContributedFromFork);

/// <summary>What the ingestor accepted, for the caller to return as a 202.</summary>
public sealed record UploadIngestResult(string BuildId, string SessionId);

/// <summary>
/// Writes one upload's session onto its build, and broadcasts the parse.
/// </summary>
public interface IUploadIngestor
{
    /// <summary>
    /// Records the upload and queues it for parsing. The caller has already decided that this
    /// request may write to this repository.
    /// </summary>
    Task<UploadIngestResult> IngestAsync(UploadIngestRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Extracted from <c>UploadsController.Upload</c> so that the fork-upload endpoint can reuse it
/// without inheriting its authentication. Nothing about the behaviour changed in the move — the
/// only branch this type takes on provenance is the document-id shape and the stored flag.
/// </remarks>
[Register(typeof(IUploadIngestor), ServiceLifetime.Scoped)]
public partial class UploadIngestor : IUploadIngestor
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<UploadIngestor> logger;

    public async Task<UploadIngestResult> IngestAsync(UploadIngestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContributedFromFork && request.PullRequestNumber is null)
            throw new ArgumentException("A fork upload must carry its pull-request number.", nameof(request));

        var repository = request.Repository;

        // ⚠️ The pull-request segment is present ONLY for fork uploads. An ordinary pull-request
        // build keeps the plain shape it has always had, because its coverage IS this repository's
        // coverage for that sha — re-keying those would orphan every stored document.
        var prSegment = request.ContributedFromFork ? request.PullRequestNumber : null;

        var commitId = Commit.DocumentId(EForgeProvider.GitHub, repository.GitHubId, request.CommitSha, prSegment);
        var commit = await session.LoadAsync<Commit>(commitId, cancellationToken);
        if (commit is null)
        {
            commit = new Commit
            {
                Sha = request.CommitSha,
                Repository = repository.Id,
                FirstSeenAtUtc = DateTimeOffset.UtcNow,
                ContributedFromFork = request.ContributedFromFork,
            };
            await session.StoreAsync(commit, commitId, cancellationToken);
        }

        commit.Branch ??= request.Branch;
        commit.PullRequestNumber ??= request.PullRequestNumber;
        // Best-effort, like the two above: the pull_request webhook is the
        // authoritative writer and uses plain assignment.
        commit.PullRequestBaseRef ??= request.BaseRef;
        commit.PullRequestBaseSha ??= request.PrBaseSha;

        // ⚠️ Never take a parent sha from a fork upload. It is the reference for the vs-parent delta
        // and a candidate baseline, it is supplied by a caller holding no credential here, and
        // nothing downstream re-derives it — `ParentShaSource` records "upload" and the trust
        // predicate then believes it. A fork commit simply has no stored parent; the forge lookup
        // fills one in later if it can.
        if (!request.ContributedFromFork
            && commit.ParentSha is null
            && !string.IsNullOrWhiteSpace(request.ParentSha))
        {
            commit.ParentSha = request.ParentSha;
            commit.ParentShaSource = "upload";
        }

        var buildId = Build.DocumentId(
            EForgeProvider.GitHub, repository.GitHubId, request.CommitSha, request.RunId, request.RunAttempt, prSegment);
        var build = await session.LoadAsync<Build>(buildId, cancellationToken);
        if (build is null)
        {
            build = new Build
            {
                Commit = commitId,
                CiRunId = request.RunId,
                CiRunAttempt = request.RunAttempt,
                Run = Build.ComposeRun(request.RunId, request.RunAttempt),
                WorkflowName = request.Workflow,
                EventName = request.EventName,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await session.StoreAsync(build, buildId, cancellationToken);
        }
        else if (build.Status == "Finalized")
        {
            // A late upload re-opens the build; the finalizer will close it again
            // and recompute — max-merge keeps this correct.
            build.Status = "Open";
            build.FinalizedAtUtc = null;
            build.FinalizeReason = null;
        }

        // One partial job makes the whole build partial: the totals under-count
        // the workspace regardless of what the other jobs measured. The declared
        // base is fixed by the first job that names one (all jobs of a run pass
        // the same inputs; ??= just makes a disagreeing straggler harmless).
        build.Partial |= request.Partial;
        build.DeclaredBaseSha ??= request.BaseSha;
        // One job whose tests failed disables carry-forward for the whole build.
        build.CarryForward &= request.CarryForward ?? true;

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var buildSession = new BuildSession
        {
            SessionId = sessionId,
            JobName = request.JobName,
            Flags = ParseFlags(request.Flags, request.ContributedFromFork),
            UploadedAtUtc = DateTime.UtcNow,
            RootDir = request.RootDir,
        };

        var attachmentNames = new List<string>();
        var index = 0;
        foreach (var file in request.Files)
        {
            var name = UploadAttachments.ReportName(sessionId, index++, file.FileName);
            session.Advanced.Attachments.Store(build, name, file.OpenReadStream());
            attachmentNames.Add(name);
        }
        if (!string.IsNullOrEmpty(request.FileList))
        {
            session.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId),
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.FileList)));
        }

        buildSession.RawFileNames = [.. attachmentNames];
        build.Sessions.Add(buildSession);
        build.LastUploadAtUtc = DateTime.UtcNow;

        await session.SaveChangesAsync(cancellationToken);
        await messageBus.BroadcastAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId }, cancellationToken);

        logger.LogInformation(
            "Accepted {Kind} upload for {Repo}@{Sha} run {RunId}.{Attempt} session {SessionId} ({Files} files)",
            request.ContributedFromFork ? "fork" : "first-party",
            repository.FullName, request.CommitSha, request.RunId, request.RunAttempt, sessionId, request.Files.Count);

        return new UploadIngestResult(buildId, sessionId);
    }

    /// <summary>Flags a first-party upload may declare in one session.</summary>
    /// <remarks>
    /// Comfortably above any real use — a monorepo labelling by language or by test tier uses a
    /// handful — and low enough that the multiplier below cannot run away.
    /// </remarks>
    private const int MaxFlagsPerSession = 32;

    /// <summary>Flags a fork upload may declare. Deliberately far tighter; see <see cref="ParseFlags"/>.</summary>
    private const int MaxForkFlagsPerSession = 4;

    /// <summary>
    /// The session's flags: de-duplicated, and capped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The flag list is a document multiplier, and it is chosen by the caller.</b> The parser
    /// writes one <c>FileCoverage</c> document per file <em>per flag</em> on top of the build-level
    /// one, so a session naming F flags over N files produces <c>N × (1 + F)</c> documents. Nothing
    /// downstream bounds N either — it is however many paths the reports mention.
    /// </para>
    /// <para>
    /// That is acceptable from an authenticated uploader, who is spending their own repository's
    /// storage and can be asked to stop. It is not acceptable from the fork endpoint, where the
    /// caller holds no credential, is not identifiable, and is spending <em>somebody else's</em>
    /// quota — so a fork session gets a much smaller cap. Excess flags are dropped rather than
    /// refused: the coverage itself is still worth recording, and a 400 here would be a confusing
    /// failure for an honest contributor whose workflow happens to label generously.
    /// </para>
    /// <para>
    /// De-duplication is free correctness rather than a bound — two identical flags compose the same
    /// document id, so they were never extra documents, only extra work.
    /// </para>
    /// </remarks>
    private static string[] ParseFlags(string? flags, bool contributedFromFork)
        => [.. (flags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(contributedFromFork ? MaxForkFlagsPerSession : MaxFlagsPerSession)];
}
