using System.Text;
using CodeCoverage.GitMirror.Git;
using CodeCoverage.GitMirror.Graph;
using CodeCoverage.GitMirror.Mirror;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitMirror.Tests._Infrastructure;

internal static class GraphFixtures
{
    public static GitRunner Runner() => new(Options.Create(new GitMirrorOptions()), NullLogger<GitRunner>.Instance);

    public static SnapshotCache Cache(GitRunner runner, long budget = 256L * 1024 * 1024)
        => new(runner, Options.Create(new GitMirrorOptions { SnapshotCacheBytes = budget }), NullLogger<SnapshotCache>.Instance);

    /// <summary>A snapshot of any bare repository (a fixture origin works as well as a mirror).</summary>
    public static async Task<CommitGraphSnapshot> OpenAsync(string repository, GraphFilter? filter = null, string repoId = "fixture")
    {
        var runner = Runner();
        var refs = await MirrorRefsState.ReadAsync(runner, repository, CancellationToken.None)
            ?? throw new InvalidOperationException("for-each-ref failed");
        return await Cache(runner).GetOrCreateAsync(new RepoKey("github", repoId), repository, refs, filter ?? new GraphFilter(null, false, null, null), CancellationToken.None);
    }

    /// <summary>
    /// The whole graph as text, one row per line: the row's cells ('*' the commit, 'v' a lane converging into it, '|' a
    /// lane passing through, '.' free), the subject, then lane, colour, up and down exactly as the wire carries them.
    /// The cells come from <c>TopAt</c>, i.e. from the checkpoint-and-replay path a page's <c>top</c> uses.
    /// </summary>
    public static async Task<string> RenderAsync(CommitGraphSnapshot snapshot)
    {
        await snapshot.EnsureRowsAsync(int.MaxValue, CancellationToken.None);
        var text = new StringBuilder();
        for (var r = 0; r < snapshot.Count; r++)
        {
            var layout = snapshot.LayoutAt(r);
            var edges = snapshot.EdgesOf(layout).ToArray();
            var cells = Enumerable.Repeat('.', layout.Width).ToArray();
            foreach (var (column, _) in snapshot.TopAt(r))
                cells[column] = '|';
            var up = edges[..layout.UpCount];
            foreach (var u in up)
                cells[u] = 'v';
            cells[layout.Lane] = '*';
            var down = Enumerable.Range(0, layout.DownCount).Select(k => $"{edges[layout.UpCount + 2 * k]}:{edges[layout.UpCount + 2 * k + 1]}");
            text.Append($"{new string(cells),-5} {snapshot.TextAt(r).Subject,-6} lane={layout.Lane} colour={layout.Colour} up=[{string.Join(',', up)}] down=[{string.Join(',', down)}]\n");
        }
        return text.ToString();
    }
}
