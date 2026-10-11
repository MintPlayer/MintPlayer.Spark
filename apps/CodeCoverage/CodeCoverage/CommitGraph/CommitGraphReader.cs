using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// The body of <c>GET /api/browse/repos/{provider}/{owner}/{name}/graph</c> (#504, PRD §4.4) for a
/// repository the caller has already been allowed to see.
/// </summary>
/// <remarks>
/// ⚠️ <b>Called only after the visibility gate.</b> Every state this answers — syncing, stale,
/// unavailable — names a repository the caller may see; reaching it for any other would make
/// "syncing" an existence oracle (PRD §5.1).
/// </remarks>
public interface ICommitGraphReader
{
    Task<CommitGraphResponse> ReadAsync(Repository repository, CommitGraphRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The graph endpoint's query parameters, as the sidecar takes them.</summary>
public sealed record CommitGraphRequest(
    int From = 0, int Count = 100, string? Ref = null, string? At = null, string? Snapshot = null,
    bool FirstParent = false, string? Grep = null, string? Author = null)
{
    /// <summary>The cap on <see cref="Count"/>, matching <c>GetCommits</c>.</summary>
    public const int MaxCount = 200;

    /// <summary>The cap on a search string, the sidecar's own.</summary>
    public const int MaxSearchLength = 200;
}

/// <summary>
/// One page of the graph, or why there is none.
/// </summary>
/// <param name="State"><c>ready</c>, <c>syncing</c>, <c>unavailable</c>, <c>stale</c> (the snapshot moved: reload at the same top SHA) or <c>not-found</c> (an <c>at=</c> SHA that is not in the walk).</param>
/// <param name="Reason">Why <c>unavailable</c>: <c>disabled</c>, <c>unsupported</c>, <c>no-access</c>, <c>installation-cap</c>, <c>too-many-refs</c>, <c>too-large</c>, <c>disk-floor</c>, <c>timeout</c>, <c>auth</c>, <c>not-found</c>, <c>error</c>.</param>
/// <param name="SnapshotId">Opaque; send it back as <c>snapshot</c> so a moved repository answers <c>stale</c> instead of a shifted page.</param>
/// <param name="TotalRows">The whole walk's row count, for the virtual scroll height.</param>
/// <param name="From">The first row's index.</param>
/// <param name="LaneCount">The widest row on this page.</param>
/// <param name="Top">Lanes active above the first row, as <c>[column, colour]</c>.</param>
/// <param name="Refs">Every branch and tag on the first page; on later pages only those pointing at its rows.</param>
/// <param name="Rows">The commits, newest first in date order.</param>
public sealed record CommitGraphResponse(
    string State,
    string? Reason,
    string? SnapshotId,
    int TotalRows,
    int From,
    int LaneCount,
    int[][] Top,
    IReadOnlyList<CommitGraphRef> Refs,
    IReadOnlyList<CommitGraphRow> Rows)
{
    public static CommitGraphResponse Empty(string state, string? reason = null, string? snapshotId = null)
        => new(state, reason, snapshotId, 0, 0, 0, [], [], []);
}

/// <summary>A branch or tag: <c>kind</c> is <c>head</c> or <c>tag</c>.</summary>
public sealed record CommitGraphRef(string Name, string Kind, string Sha);

/// <summary>
/// One commit. ⚠️ Carries the author's name and an opaque avatar key, never the email (D28).
/// </summary>
/// <param name="Up">Columns whose lines converge into this commit.</param>
/// <param name="Down">One <c>[column, colour]</c> per parent edge leaving it.</param>
/// <param name="Refs">Names of the refs pointing at it.</param>
/// <param name="Coverage">Null when no upload ever reached this commit.</param>
/// <param name="AvatarKey">For <c>GET /api/browse/avatar/{key}</c>; null means initials.</param>
public sealed record CommitGraphRow(
    string Sha,
    string[] Parents,
    string Subject,
    string AuthorName,
    DateTimeOffset Date,
    int Lane,
    int Colour,
    int[] Up,
    int[][] Down,
    string[] Refs,
    CommitGraphCoverage? Coverage,
    string? AvatarKey);

/// <summary>The coverage overlay of one commit.</summary>
/// <param name="LinesCovered">Null while the commit has a record but no assembled report (pending).</param>
/// <param name="Percent">Covered over coverable lines × 100; null for 0/0 or pending.</param>
/// <param name="DeltaVsParent">Percentage points against the parent, when known.</param>
/// <param name="Completeness"><c>Complete</c> or <c>Partial</c>, as assembled; null for commits that predate assemblies.</param>
public sealed record CommitGraphCoverage(
    int? LinesCovered, int? LinesCoverable, double? Percent, double? DeltaVsParent, string? Completeness);

/// <remarks>
/// <para>
/// <b>Reads never wait for git.</b> A missing mirror queues a clone and answers <c>syncing</c>; a
/// stale one queues a fetch and serves the rows it has (D23). Only this read creates a mirror
/// (D10), and it stamps <see cref="RepositoryGraphState.LastViewedAtUtc"/> for the eviction order,
/// at most once a minute so that scrolling does not write a document per page.
/// </para>
/// <para>
/// <b>The overlay is one load, no query</b> (PRD §4.4, S6): commit ids are deterministic
/// (<see cref="Commit.DocumentId"/>), so a page's coverage is a single <c>LoadAsync</c> and no RQL is
/// built at all.
/// </para>
/// </remarks>
[Register(typeof(ICommitGraphReader), ServiceLifetime.Scoped)]
public partial class CommitGraphReader : ICommitGraphReader
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ICommitGraphSidecar sidecar;
    [Inject] private readonly ICommitGraphAvatars avatars;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IOptions<CommitGraphOptions> options;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<CommitGraphReader> logger;

    private static readonly TimeSpan ViewStampInterval = TimeSpan.FromMinutes(1);

    public async Task<CommitGraphResponse> ReadAsync(Repository repository, CommitGraphRequest request, CancellationToken cancellationToken = default)
    {
        if (!sidecar.IsConfigured) return CommitGraphResponse.Empty("unavailable", "disabled");
        if (repository.Id is not { } repositoryId || !CommitGraphSidecarClient.TryPath(repositoryId, out _))
            return CommitGraphResponse.Empty("unavailable", "unsupported");
        if (forges.For(repository.Provider) is not { } forge || !forge.Capabilities.Contains(EForgeCapability.GitMirror))
            return CommitGraphResponse.Empty("unavailable", "unsupported");

        var o = options.Value;
        var now = time.GetUtcNow();
        var state = await StampViewAsync(repositoryId, now.UtcDateTime, cancellationToken);

        if (!state.HasMirror)
        {
            // A clone is under way, unless it was asked for so long ago that it must have been lost.
            var lostAfter = TimeSpan.FromSeconds(Math.Max(o.Installed.TimeoutSeconds, o.Anonymous.TimeoutSeconds)) + TimeSpan.FromMinutes(5);
            if (state.Syncing && state.SyncingSinceUtc > now.UtcDateTime - lostAfter)
                return CommitGraphResponse.Empty("syncing");

            if (state.Outcome is "refused" or "failed"
                && state.LastFetchAtUtc > now.UtcDateTime.AddMinutes(-o.RetryAfterMinutes))
                return CommitGraphResponse.Empty("unavailable", state.Reason ?? state.Outcome);

            await RequestCloneAsync(repositoryId, now, cancellationToken);
            return CommitGraphResponse.Empty("syncing");
        }

        SidecarGraphResult result;
        try
        {
            result = await sidecar.GetGraphAsync(repositoryId, new SidecarGraphQuery(
                Math.Max(0, request.From),
                Math.Clamp(request.Count, 1, CommitGraphRequest.MaxCount),
                NullIfEmpty(request.Ref),
                NullIfEmpty(request.At),
                NullIfEmpty(request.Snapshot),
                request.FirstParent,
                Truncate(request.Grep),
                Truncate(request.Author)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The mirror sidecar could not serve the graph of {FullName}", repository.FullName);
            return CommitGraphResponse.Empty("unavailable", "error");
        }

        switch (result.Status)
        {
            case SidecarGraphStatus.Missing:
                // The state said there was a mirror; the sidecar has none (a lost volume). Same path
                // as a first view.
                await RequestCloneAsync(repositoryId, now, cancellationToken, mirrorLost: true);
                return CommitGraphResponse.Empty("syncing");
            case SidecarGraphStatus.Stale:
                return CommitGraphResponse.Empty("stale", snapshotId: result.SnapshotId);
            case SidecarGraphStatus.NotFound:
                return CommitGraphResponse.Empty("not-found");
        }

        // Stale-on-view (D23): serve what there is, refresh behind it.
        if (!state.Syncing && (state.LastFetchAtUtc ?? DateTime.MinValue) < now.UtcDateTime.AddMinutes(-o.StaleAfterMinutes))
            await messageBus.BroadcastOnceAsync(
                new FetchCommitGraphMessage { RepositoryId = repositoryId, AllowClone = false },
                CommitGraphTriggers.ViewKey(repositoryId, now, TimeSpan.FromMinutes(o.StaleAfterMinutes)), cancellationToken);

        return await BuildAsync(repository, result.Page!, cancellationToken);
    }

    private async Task<CommitGraphResponse> BuildAsync(Repository repository, SidecarGraphPage page, CancellationToken cancellationToken)
    {
        // One load for the page's coverage; ids nobody uploaded for come back null.
        var ids = page.Rows.ToDictionary(r => r.Sha, r => Commit.DocumentId(repository.Provider, repository.GitHubId, r.Sha), StringComparer.Ordinal);
        Dictionary<string, Commit> commits = ids.Count == 0
            ? []
            : await session.LoadAsync<Commit>(ids.Values.Distinct(), cancellationToken);

        var avatarKeys = await avatars.ResolveKeysAsync(repository,
            [.. page.Rows.Where(r => !string.IsNullOrEmpty(r.AuthorEmail)).Select(r => new AvatarLookup(r.AuthorEmail!, r.Sha))],
            cancellationToken);

        var refsBySha = page.Refs
            .GroupBy(r => r.Sha, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Name).ToArray(), StringComparer.Ordinal);

        var rows = page.Rows.Select(r => new CommitGraphRow(
            r.Sha,
            r.Parents ?? [],
            r.Subject ?? string.Empty,
            r.AuthorName ?? string.Empty,
            r.Date,
            r.Lane,
            r.Colour,
            r.Up ?? [],
            r.Down ?? [],
            refsBySha.GetValueOrDefault(r.Sha) ?? [],
            Overlay(commits.GetValueOrDefault(ids[r.Sha])),
            r.AuthorEmail is { Length: > 0 } email ? avatarKeys.GetValueOrDefault(email) : null)).ToList();

        return new CommitGraphResponse(
            "ready", null, page.SnapshotId, page.TotalRows, page.From, page.LaneCount, page.Top ?? [],
            [.. page.Refs.Select(r => new CommitGraphRef(r.Name, r.Kind, r.Sha))], rows);
    }

    private static CommitGraphCoverage? Overlay(Commit? commit)
    {
        // A fork's commit carries this repository's id but is not its history (as in GetCommits).
        if (commit is null || commit.ContributedFromFork) return null;
        var summary = commit.Coverage;
        double? percent = summary is { LinesCoverable: > 0 } s ? 100.0 * s.LinesCovered / s.LinesCoverable : null;
        return new CommitGraphCoverage(summary?.LinesCovered, summary?.LinesCoverable, percent,
            commit.CoverageDeltaVsParent, commit.AssemblyCompleteness);
    }

    /// <summary>Loads or creates the state and stamps the view, under a change-vector check.</summary>
    private async Task<RepositoryGraphState> StampViewAsync(string repositoryId, DateTime now, CancellationToken cancellationToken)
    {
        var stateId = RepositoryGraphState.ForRepository(repositoryId);
        for (var attempt = 0; ; attempt++)
        {
            using var writer = store.OpenAsyncSession();
            writer.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var state = await writer.LoadAsync<RepositoryGraphState>(stateId, cancellationToken);
            if (state is null)
            {
                state = new RepositoryGraphState { Repository = repositoryId, LastViewedAtUtc = now };
                await writer.StoreAsync(state, changeVector: string.Empty, stateId, cancellationToken);
            }
            else if (state.LastViewedAtUtc is { } seen && now - seen < ViewStampInterval)
            {
                return state;
            }
            else
            {
                state.LastViewedAtUtc = now;
            }

            try
            {
                await writer.SaveChangesAsync(cancellationToken);
                return state;
            }
            catch (ConcurrencyException) when (attempt < 4)
            {
            }
        }
    }

    /// <summary>Marks the state syncing and queues the one kind of fetch that may create a mirror.</summary>
    private async Task RequestCloneAsync(string repositoryId, DateTimeOffset now, CancellationToken cancellationToken, bool mirrorLost = false)
    {
        var stateId = RepositoryGraphState.ForRepository(repositoryId);
        for (var attempt = 0; ; attempt++)
        {
            using var writer = store.OpenAsyncSession();
            writer.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var state = await writer.LoadAsync<RepositoryGraphState>(stateId, cancellationToken);
            if (state is null) return;
            if (mirrorLost) state.HasMirror = false;
            state.Syncing = true;
            state.SyncingSinceUtc = now.UtcDateTime;
            try
            {
                await writer.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (ConcurrencyException) when (attempt < 4)
            {
            }
        }

        var window = TimeSpan.FromMinutes(Math.Max(1, options.Value.StaleAfterMinutes));
        await messageBus.BroadcastOnceAsync(
            new FetchCommitGraphMessage { RepositoryId = repositoryId, AllowClone = true },
            $"{CommitGraphTriggers.ViewKey(repositoryId, now, window)}:clone", cancellationToken);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Truncate(string? value)
        => NullIfEmpty(value) is { } v ? v[..Math.Min(v.Length, CommitGraphRequest.MaxSearchLength)] : null;
}
