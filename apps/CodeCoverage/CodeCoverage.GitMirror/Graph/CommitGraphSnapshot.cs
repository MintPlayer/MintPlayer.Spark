using System.Security.Cryptography;
using System.Text;
using CodeCoverage.Git;
using CodeCoverage.GitMirror.Mirror;

namespace CodeCoverage.GitMirror.Graph;

/// <summary>
/// A lazily built commit graph (PRD §4.3, D21, D22, D27), keyed <c>(repoId, refsHash, filter)</c>.
/// <list type="bullet">
/// <item>It holds the rows laid out so far (sha, parents, text, lane, colour, edges), a sha → row map, and a lane-state
/// checkpoint every <see cref="CheckpointInterval"/> rows. Pass-through lanes are not stored: a page carries the lanes at
/// its top edge (<c>top</c>, from the nearest checkpoint plus a short replay) and the client derives the rest.</item>
/// <item>A request past the frontier extends it from the open <c>git log</c> reader; rows above are never redone. A
/// reader closed while idle resumes with <c>--skip={rows read}</c>, which walks the same order because the refs hash is
/// part of the key.</item>
/// <item>Every member is used under <see cref="Sync"/>.</item>
/// </list>
/// </summary>
public sealed class CommitGraphSnapshot
{
    public const int CheckpointInterval = 500;

    private readonly GitRunner git;
    private readonly CommitStore store = new();
    private readonly LaneLayout layout;
    private readonly List<RowLayout> rows = [];
    private readonly List<int> edges = [];
    private readonly List<byte[]> checkpoints = [];
    private GitReadStream? reader;
    private CommitGraphReader? parser;
    private long bytes;

    internal CommitGraphSnapshot(GitRunner git, RepoKey key, string mirror, MirrorRefsState refs, GraphFilter filter, long totalRows)
    {
        this.git = git;
        layout = new LaneLayout(store);
        Key = key;
        Mirror = mirror;
        RefsHash = refs.RefsHash;
        Refs = refs.Refs;
        Filter = filter;
        TotalRows = totalRows;
        SnapshotId = IdOf(key, refs.RefsHash, filter);
        Complete = refs.Refs.Count == 0 || totalRows == 0;
        bytes = 1024 + refs.Refs.Sum(r => 2L * (r.Name.Length + r.Sha.Length) + 64);
    }

    public RepoKey Key { get; }
    public string Mirror { get; }
    public string RefsHash { get; }
    public IReadOnlyList<MirrorRef> Refs { get; }
    public GraphFilter Filter { get; }
    public long TotalRows { get; }
    public string SnapshotId { get; }

    /// <summary>The walk reached its end: every row is laid out.</summary>
    public bool Complete { get; private set; }

    public int Count => store.Count;

    /// <summary>The estimated bytes held, text included (D22, D27).</summary>
    public long Bytes => Interlocked.Read(ref bytes);

    public bool ReaderOpen => reader is not null;

    public SemaphoreSlim Sync { get; } = new(1, 1);

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the cache dropped the snapshot; a holder closes the reader when it is done.</summary>
    public bool Evicted { get; set; }

    public static string IdOf(RepoKey key, string refsHash, GraphFilter filter)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}\n{refsHash}\n{filter.CacheKey}")))[..32];

    /// <summary>The <c>git log</c> arguments of the walk (D25: no mailmap; date order, PRD §4.3).</summary>
    internal IReadOnlyList<string> LogArguments(int skip)
    {
        var args = new List<string> { "log", "--no-mailmap", "--encoding=UTF-8", "--date-order", "-z", CommitGraphReader.Format };
        args.AddRange(Filter.LimitArguments());
        if (skip > 0)
            args.Add($"--skip={skip}");
        args.AddRange(Filter.RevisionArguments());
        return args;
    }

    /// <summary>Lays out rows until <paramref name="target"/> rows exist or the walk ends.</summary>
    public async Task EnsureRowsAsync(int target, CancellationToken cancellationToken)
    {
        while (store.Count < target && !Complete)
        {
            if (!await ReadOneAsync(cancellationToken))
                break;
        }
    }

    /// <summary>The row of <paramref name="sha"/> (a full SHA), extending as far as needed; -1 when it is not in the walk.</summary>
    public async Task<int> FindAsync(string sha, CancellationToken cancellationToken)
    {
        while (true)
        {
            var row = store.RowOf(sha);
            if (row >= 0)
                return row;
            if (Complete || !await ReadOneAsync(cancellationToken))
                return -1;
        }
    }

    private async Task<bool> ReadOneAsync(CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            reader = git.OpenRead(new GitInvocation
            {
                GitDirectory = Mirror,
                Read = true,
                Arguments = LogArguments(store.Count),
                Timeout = TimeSpan.FromMinutes(10),
            });
            parser = new CommitGraphReader(reader.Output);
        }

        var record = await parser!.ReadAsync(cancellationToken);
        if (record is null)
        {
            Complete = true;
            await CloseReaderAsync();
            return false;
        }

        var r = store.Count;
        if (r % CheckpointInterval == 0)
        {
            var checkpoint = layout.Checkpoint();
            checkpoints.Add(checkpoint);
            Interlocked.Add(ref bytes, checkpoint.Length + 32);
        }
        var added = store.Add(record.Value);
        var before = edges.Count;
        rows.Add(layout.Step(store.IdAt(r), LayoutParents(r), edges));
        Interlocked.Add(ref bytes, added + 32 + 4L * (edges.Count - before));
        return true;
    }

    public async Task CloseReaderAsync()
    {
        if (reader is null)
            return;
        var open = reader;
        reader = null;
        parser = null;
        await open.DisposeAsync();
    }

    /// <summary>The lanes occupied at the top edge of <paramref name="row"/>: the live state at the frontier, else the
    /// nearest checkpoint above replayed down to the row.</summary>
    public List<(int Column, int Colour)> TopAt(int row)
    {
        var lanes = new List<(int, int)>();
        if (row >= store.Count)
        {
            layout.ActiveLanes(lanes);
            return lanes;
        }
        var replay = LaneLayout.Restore(store, checkpoints[row / CheckpointInterval]);
        var scratch = new List<int>();
        for (var r = replay.Row; r < row; r++)
        {
            scratch.Clear();
            replay.Step(store.IdAt(r), LayoutParents(r), scratch);
        }
        replay.ActiveLanes(lanes);
        return lanes;
    }

    /// <summary>The parents the layout draws edges to: all of them; only the first in a first-parent walk (the others are
    /// never walked, so their lanes would dangle); none in a search, which is a flat list (<see cref="GraphFilter.IsSearch"/>).
    /// The row's <c>parents</c> on the wire always lists all of them.</summary>
    private ReadOnlySpan<int> LayoutParents(int row)
    {
        if (Filter.IsSearch)
            return [];
        var parents = store.ParentsAt(row);
        return Filter.FirstParent && parents.Length > 1 ? parents.AsSpan(0, 1) : parents;
    }

    public RowLayout LayoutAt(int row) => rows[row];

    public ReadOnlySpan<int> EdgesOf(RowLayout row)
        => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(edges).Slice(row.EdgeStart, row.UpCount + 2 * row.DownCount);

    public string ShaAt(int row) => store.ShaAt(row);

    public IEnumerable<string> ParentShasAt(int row) => store.ParentsAt(row).Select(store.Sha);

    public CommitText TextAt(int row) => store.TextAt(row);
}
