using CodeCoverage.Git;
using CodeCoverage.GitMirror.Graph;
using CodeCoverage.GitMirror.Mirror;
using CodeCoverage.GitMirror.Tests._Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitMirror.Tests.Graph;

/// <summary>
/// The snapshot cache (D22): bounded by bytes with the least recently used dropped first and the one in use spared,
/// idle readers closed without dropping their snapshots, a failed creation not cached, and invalidation per repository.
/// </summary>
public sealed class SnapshotCacheTests : IDisposable
{
    private static readonly GraphFilter All = new(null, false, null, null);

    private readonly TempDir dir = new("cache");
    private readonly string repository;
    private readonly GitRunner runner = GraphFixtures.Runner();

    public SnapshotCacheTests()
    {
        repository = dir.Sub("origin.git");
        new RepoBuilder()
            .Commit("a", 0)
            .Commit("b", 1, "a")
            .Commit("c", 2, "b")
            .Commit("d", 3, "c")
            .Branch("main", "d")
            .WriteTo(repository);
    }

    public void Dispose() => dir.Dispose();

    private SnapshotCache NewCache(long budget = 256L * 1024 * 1024, int idleSeconds = 60)
        => new(runner, Options.Create(new GitMirrorOptions { SnapshotCacheBytes = budget, ReaderIdleSeconds = idleSeconds }), NullLogger<SnapshotCache>.Instance);

    private async Task<MirrorRefsState> RefsAsync()
        => await MirrorRefsState.ReadAsync(runner, repository, CancellationToken.None) ?? throw new InvalidOperationException("for-each-ref failed");

    /// <summary>A snapshot with two rows read and its reader still open (the walk has four).</summary>
    private static async Task<CommitGraphSnapshot> OpenPartAsync(SnapshotCache cache, string repoId, string mirror, MirrorRefsState refs)
    {
        var snapshot = await cache.GetOrCreateAsync(new RepoKey("github", repoId), mirror, refs, All, CancellationToken.None);
        await snapshot.EnsureRowsAsync(2, CancellationToken.None);
        return snapshot;
    }

    [Fact]
    public async Task Over_budget_drops_the_least_recently_used_and_spares_the_one_in_use()
    {
        var refs = await RefsAsync();
        var probe = NewCache();
        var size = (await OpenPartAsync(probe, "probe", repository, refs)).Bytes;
        await probe.DisposeAsync();

        var cache = NewCache(budget: 2 * size);
        var oldest = await OpenPartAsync(cache, "oldest", repository, refs);
        var older = await OpenPartAsync(cache, "older", repository, refs);
        var newest = await OpenPartAsync(cache, "newest", repository, refs);
        oldest.LastUsedUtc = DateTime.UtcNow.AddMinutes(-3);
        older.LastUsedUtc = DateTime.UtcNow.AddMinutes(-2);
        newest.LastUsedUtc = DateTime.UtcNow.AddMinutes(-1);
        cache.TotalBytes.Should().Be(3 * size);

        await cache.TrimAsync(oldest);

        cache.Count.Should().Be(2);
        cache.TotalBytes.Should().Be(2 * size);
        older.Evicted.Should().BeTrue();
        older.ReaderOpen.Should().BeFalse("a dropped snapshot's git log must not outlive it");
        oldest.Evicted.Should().BeFalse("the snapshot in use is never dropped under its request");
        newest.Evicted.Should().BeFalse();
        oldest.ReaderOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Within_budget_trims_nothing()
    {
        var refs = await RefsAsync();
        await using var cache = NewCache();
        var snapshot = await OpenPartAsync(cache, "one", repository, refs);

        await cache.TrimAsync(null);

        cache.Count.Should().Be(1);
        snapshot.Evicted.Should().BeFalse();
    }

    [Fact]
    public async Task Idle_readers_close_and_their_snapshots_stay()
    {
        var refs = await RefsAsync();
        await using var cache = NewCache(idleSeconds: 60);
        var idle = await OpenPartAsync(cache, "idle", repository, refs);
        var busy = await OpenPartAsync(cache, "busy", repository, refs);
        var recent = await OpenPartAsync(cache, "recent", repository, refs);
        idle.LastUsedUtc = busy.LastUsedUtc = DateTime.UtcNow.AddMinutes(-5);
        await busy.Sync.WaitAsync();

        await cache.CloseIdleReadersAsync();

        busy.Sync.Release();
        idle.ReaderOpen.Should().BeFalse();
        busy.ReaderOpen.Should().BeTrue("a snapshot a request holds is skipped, not waited for");
        recent.ReaderOpen.Should().BeTrue();
        cache.Count.Should().Be(3);

        // The closed snapshot resumes where it stopped (--skip): the full walk is still four rows.
        await idle.EnsureRowsAsync(int.MaxValue, CancellationToken.None);
        idle.Count.Should().Be(4);
        idle.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_creation_is_not_cached()
    {
        var refs = await RefsAsync();
        await using var cache = NewCache();
        var key = new RepoKey("github", "broken");
        var notARepository = dir.Sub("missing.git");
        Directory.CreateDirectory(notARepository);

        var failing = () => cache.GetOrCreateAsync(key, notARepository, refs, All, CancellationToken.None);

        (await failing.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("rev-list --count failed");
        cache.Count.Should().Be(0);

        // Same key, refs and filter: a cached failure would answer again; a fresh creation succeeds.
        var snapshot = await cache.GetOrCreateAsync(key, repository, refs, All, CancellationToken.None);
        snapshot.TotalRows.Should().Be(4);
    }

    [Fact]
    public async Task A_repository_without_refs_is_an_empty_snapshot_without_running_git()
    {
        await using var cache = NewCache();

        var snapshot = await cache.GetOrCreateAsync(new RepoKey("github", "empty"), dir.Sub("nowhere.git"), new MirrorRefsState("none", []), All, CancellationToken.None);

        snapshot.TotalRows.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_snapshot()
    {
        var refs = await RefsAsync();
        await using var cache = NewCache();
        var key = new RepoKey("github", "shared");

        var both = await Task.WhenAll(
            cache.GetOrCreateAsync(key, repository, refs, All, CancellationToken.None),
            cache.GetOrCreateAsync(key, repository, refs, All, CancellationToken.None));

        both[0].Should().BeSameAs(both[1]);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public async Task Invalidation_keeps_the_current_refs_and_other_repositories()
    {
        var refs = await RefsAsync();
        await using var cache = NewCache();
        var current = await OpenPartAsync(cache, "repo", repository, refs);
        var stale = await OpenPartAsync(cache, "repo", repository, refs with { RefsHash = "stale" });
        var other = await OpenPartAsync(cache, "other", repository, refs with { RefsHash = "stale" });

        await cache.InvalidateAsync(new RepoKey("github", "repo"), refs.RefsHash);

        stale.Evicted.Should().BeTrue();
        stale.ReaderOpen.Should().BeFalse();
        current.Evicted.Should().BeFalse();
        other.Evicted.Should().BeFalse();

        await cache.InvalidateAsync(new RepoKey("github", "repo"), null);

        current.Evicted.Should().BeTrue();
        cache.Count.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_closes_every_reader()
    {
        var refs = await RefsAsync();
        var cache = NewCache();
        var one = await OpenPartAsync(cache, "one", repository, refs);
        var two = await OpenPartAsync(cache, "two", repository, refs);

        await cache.DisposeAsync();

        one.ReaderOpen.Should().BeFalse();
        two.ReaderOpen.Should().BeFalse();
        cache.Count.Should().Be(0);
    }
}
