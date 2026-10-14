using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.GitMirror.Mirror;

namespace CodeCoverage.GitMirror.Graph;

// The wire shapes of GET /repos/{provider}/{repoId}/graph (the sidecar contract). JSON camelCase.

public sealed record GraphPage(
    string SnapshotId,
    string RefsHash,
    long TotalRows,
    int From,
    int LaneCount,
    int[][] Top,
    IReadOnlyList<GraphRow> Rows,
    IReadOnlyList<MirrorRef> Refs);

/// <summary>One commit row. <see cref="AuthorEmail"/> is for the app's avatar resolution only; the app never forwards it
/// to the browser (D28).</summary>
public sealed record GraphRow(
    string Sha,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    string Date,
    string Subject,
    int Lane,
    int Colour,
    int[] Up,
    int[][] Down);

public sealed record GraphQuery(int From, int Count, GraphFilter Filter, string? At, string? Snapshot);

public abstract record GraphOutcome
{
    public sealed record Missing : GraphOutcome;
    public sealed record NotFound : GraphOutcome;
    public sealed record Stale(string SnapshotId) : GraphOutcome;
    public sealed record Page(GraphPage Value) : GraphOutcome;
}

/// <summary>Serves graph pages from the snapshot of the mirror's current refs (PRD §4.3, D21).</summary>
public sealed partial class GraphService(GitRunner git, MirrorPaths paths, SnapshotCache cache)
{
    public const int MaxCount = 200;

    [GeneratedRegex("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPrefix();

    public static bool IsShaPrefix(string value) => ShaPrefix().IsMatch(value);

    public async Task<GraphOutcome> GetPageAsync(RepoKey key, GraphQuery query, CancellationToken cancellationToken)
    {
        if (!paths.Exists(key))
            return new GraphOutcome.Missing();
        var mirror = paths.Mirror(key);
        var refs = await MirrorRefsState.ReadAsync(git, mirror, cancellationToken);
        if (refs is null)
            return new GraphOutcome.Missing();
        if (query.Filter.Ref is { } name && refs.Refs.All(r => r.Name != name))
            return new GraphOutcome.NotFound();

        // D21: a client holding a snapshot of older refs gets 410 and reloads at its top SHA with at=.
        var currentId = CommitGraphSnapshot.IdOf(key, refs.RefsHash, query.Filter);
        if (query.Snapshot is not null && query.Snapshot != currentId)
            return new GraphOutcome.Stale(currentId);

        string? atSha = null;
        if (query.At is not null)
        {
            var resolved = await git.RunAsync(new GitInvocation
            {
                GitDirectory = mirror,
                Read = true,
                Arguments = ["rev-parse", "--verify", "--quiet", "--end-of-options", query.At + "^{commit}"],
            }, cancellationToken);
            atSha = resolved.Succeeded ? resolved.StandardOutput.Trim() : null;
            if (atSha is null || atSha.Length != 40)
                return new GraphOutcome.NotFound();
        }

        var snapshot = await cache.GetOrCreateAsync(key, mirror, refs, query.Filter, cancellationToken);
        GraphPage page;
        await snapshot.Sync.WaitAsync(cancellationToken);
        try
        {
            var from = query.From;
            if (atSha is not null)
            {
                var row = await snapshot.FindAsync(atSha, cancellationToken);
                if (row < 0)
                    return new GraphOutcome.NotFound();
                from = row / query.Count * query.Count;
            }
            await snapshot.EnsureRowsAsync(from + query.Count, cancellationToken);
            page = BuildPage(snapshot, from, query.Count);
            snapshot.LastUsedUtc = DateTime.UtcNow;
            if (snapshot.Evicted)
                await snapshot.CloseReaderAsync();
        }
        finally
        {
            snapshot.Sync.Release();
        }
        await cache.TrimAsync(snapshot);
        return new GraphOutcome.Page(page);
    }

    internal static GraphPage BuildPage(CommitGraphSnapshot snapshot, int from, int count)
    {
        var end = Math.Min(from + count, snapshot.Count);
        var top = snapshot.TopAt(Math.Min(from, snapshot.Count));
        var laneCount = top.Count == 0 ? 0 : top[^1].Column + 1;
        var rows = new List<GraphRow>(Math.Max(0, end - from));
        for (var r = from; r < end; r++)
        {
            var layout = snapshot.LayoutAt(r);
            var edges = snapshot.EdgesOf(layout);
            var up = edges[..layout.UpCount].ToArray();
            var down = new int[layout.DownCount][];
            for (var k = 0; k < layout.DownCount; k++)
                down[k] = [edges[layout.UpCount + 2 * k], edges[layout.UpCount + 2 * k + 1]];
            var text = snapshot.TextAt(r);
            rows.Add(new GraphRow(snapshot.ShaAt(r), snapshot.ParentShasAt(r).ToList(), text.AuthorName, text.AuthorEmail, text.Date, text.Subject,
                layout.Lane, layout.Colour, up, down));
            laneCount = Math.Max(laneCount, layout.Width);
        }

        // All refs on the first page (for the pills and the branch selector), else only those on the returned rows.
        IReadOnlyList<MirrorRef> refs = snapshot.Refs;
        if (from != 0)
        {
            var shas = rows.Select(r => r.Sha).ToHashSet(StringComparer.Ordinal);
            refs = snapshot.Refs.Where(r => shas.Contains(r.Sha)).ToList();
        }

        return new GraphPage(snapshot.SnapshotId, snapshot.RefsHash, snapshot.TotalRows, from, laneCount,
            top.Select(t => new[] { t.Column, t.Colour }).ToArray(), rows, refs);
    }
}
