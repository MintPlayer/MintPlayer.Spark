using CodeCoverage.GitMirror.Git;
using CodeCoverage.GitMirror.Mirror;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitMirror.Graph;

/// <summary>
/// The snapshots in memory, bounded by bytes, not by count (D22): subjects and author text are counted, being about 90% of
/// a snapshot (D27). Least recently used snapshots are dropped first; the one a request is using is never dropped under
/// it. Concurrent requests for the same key share one creation.
/// </summary>
public sealed class SnapshotCache(GitRunner git, IOptions<GitMirrorOptions> options, ILogger<SnapshotCache> logger) : IAsyncDisposable
{
    private readonly Dictionary<string, Task<CommitGraphSnapshot>> entries = [];

    public long BudgetBytes => options.Value.SnapshotCacheBytes;

    public async Task<CommitGraphSnapshot> GetOrCreateAsync(RepoKey key, string mirror, MirrorRefsState refs, GraphFilter filter, CancellationToken cancellationToken)
    {
        var id = CommitGraphSnapshot.IdOf(key, refs.RefsHash, filter);
        Task<CommitGraphSnapshot> entry;
        lock (entries)
        {
            if (!entries.TryGetValue(id, out entry!))
                entries[id] = entry = CreateAsync(key, mirror, refs, filter);
        }
        try
        {
            var snapshot = await entry.WaitAsync(cancellationToken);
            snapshot.LastUsedUtc = DateTime.UtcNow;
            return snapshot;
        }
        catch (Exception) when (entry.IsFaulted)
        {
            lock (entries)
            {
                if (entries.TryGetValue(id, out var current) && current == entry)
                    entries.Remove(id);
            }
            throw;
        }
    }

    private async Task<CommitGraphSnapshot> CreateAsync(RepoKey key, string mirror, MirrorRefsState refs, GraphFilter filter)
    {
        await Task.Yield();
        long total = 0;
        if (refs.Refs.Count > 0)
        {
            // D21: the virtual scroll's height before the lazy walk has ended, with the walk's own filter.
            var args = new List<string> { "rev-list", "--count" };
            args.AddRange(filter.LimitArguments());
            args.AddRange(filter.RevisionArguments());
            var count = await git.RunAsync(new GitInvocation { GitDirectory = mirror, Read = true, Arguments = args, Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);
            if (!count.Succeeded || !long.TryParse(count.StandardOutput.Trim(), out total))
                throw new InvalidOperationException($"rev-list --count failed for {key} (exit {count.ExitCode}).");
        }
        return new CommitGraphSnapshot(git, key, mirror, refs, filter, total);
    }

    /// <summary>Drops snapshots of <paramref name="key"/> whose refs hash is not <paramref name="keepRefsHash"/> (all of
    /// them when null), closing their readers so the mirror can be deleted.</summary>
    public async Task InvalidateAsync(RepoKey key, string? keepRefsHash)
    {
        foreach (var snapshot in Drop(s => s.Key == key && s.RefsHash != keepRefsHash))
            await CloseAsync(snapshot);
    }

    /// <summary>Drops least recently used snapshots until the total is within the budget, sparing
    /// <paramref name="inUse"/>.</summary>
    public async Task TrimAsync(CommitGraphSnapshot? inUse)
    {
        var ready = Ready();
        var total = ready.Sum(s => s.Bytes);
        if (total <= BudgetBytes)
            return;
        var victims = new List<CommitGraphSnapshot>();
        foreach (var snapshot in ready.Where(s => s != inUse).OrderBy(s => s.LastUsedUtc))
        {
            if (total <= BudgetBytes)
                break;
            victims.Add(snapshot);
            total -= snapshot.Bytes;
        }
        var dropped = Drop(victims.Contains);
        if (dropped.Count > 0)
            logger.LogInformation("Snapshot cache over budget: dropped {Count} snapshot(s)", dropped.Count);
        foreach (var snapshot in dropped)
            await CloseAsync(snapshot);
    }

    /// <summary>Closes readers idle longer than the configured time; their snapshots stay and resume with --skip.</summary>
    public async Task CloseIdleReadersAsync()
    {
        var idle = DateTime.UtcNow - TimeSpan.FromSeconds(options.Value.ReaderIdleSeconds);
        foreach (var snapshot in Ready().Where(s => s.ReaderOpen && s.LastUsedUtc < idle))
        {
            if (!await snapshot.Sync.WaitAsync(0))
                continue;
            try
            {
                await snapshot.CloseReaderAsync();
            }
            finally
            {
                snapshot.Sync.Release();
            }
        }
    }

    /// <summary>On shutdown: no git log process outlives the host.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var snapshot in Drop(_ => true))
            await snapshot.CloseReaderAsync();
    }

    public long TotalBytes => Ready().Sum(s => s.Bytes);

    public int Count => Ready().Count;

    private List<CommitGraphSnapshot> Ready()
    {
        lock (entries)
            return entries.Values.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
    }

    private List<CommitGraphSnapshot> Drop(Func<CommitGraphSnapshot, bool> predicate)
    {
        var dropped = new List<CommitGraphSnapshot>();
        lock (entries)
        {
            foreach (var (id, task) in entries.ToList())
            {
                if (task.IsCompletedSuccessfully && predicate(task.Result))
                {
                    entries.Remove(id);
                    task.Result.Evicted = true;
                    dropped.Add(task.Result);
                }
            }
        }
        return dropped;
    }

    private static async Task CloseAsync(CommitGraphSnapshot snapshot)
    {
        await snapshot.Sync.WaitAsync();
        try
        {
            await snapshot.CloseReaderAsync();
        }
        finally
        {
            snapshot.Sync.Release();
        }
    }
}

/// <summary>Closes idle <c>git log</c> readers periodically, so open processes do not pile up.</summary>
public sealed class IdleReaderSweeper(SnapshotCache cache) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await cache.CloseIdleReadersAsync();
    }
}
