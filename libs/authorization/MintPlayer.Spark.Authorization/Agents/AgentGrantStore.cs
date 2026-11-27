using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// Reads and writes <see cref="AgentGrant"/>s (MCP PRD D12, D15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read per call, cached a few seconds.</b> Every <c>/mcp</c> call needs its grant twice (the token check and
/// the permission layer); <see cref="GetAsync"/> serves both from a <see cref="CacheFor"/> cache. An edit or a
/// revocation through this store invalidates its entry, so on this node it applies to the very next call; on
/// another node within <see cref="CacheFor"/>. The user's security stamp is <b>not</b> cached (the token check
/// reads the user on every call), so a password change revokes at once everywhere.
/// </para>
/// <para>
/// <b>No index, no query.</b> A user's grants are a prefix load of their natural ids
/// (<c>LoadStartingWith</c>), consistent the moment they are written.
/// </para>
/// </remarks>
internal sealed partial class AgentGrantStore(ISparkStore store, TimeProvider time, ILogger<AgentGrantStore> logger)
{
    /// <summary>How long a grant read is reused.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>How often <see cref="AgentGrant.LastUsedAt"/> is written at most (D15).</summary>
    internal static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (AgentGrant? Grant, DateTimeOffset LoadedAt)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> touched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The grant, from the cache when it is younger than <see cref="CacheFor"/>. Treat it as read-only.</summary>
    public async Task<AgentGrant?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (cache.TryGetValue(id, out var hit) && now - hit.LoadedAt < CacheFor)
            return hit.Grant;

        using var session = store.OpenSession();
        var grant = await session.LoadAsync<AgentGrant>(id, cancellationToken);
        cache[id] = (grant, now);

        if (cache.Count > 10_000)
            Prune(now);
        return grant;
    }

    /// <summary>Every grant of <paramref name="userId"/>, read now (no cache).</summary>
    public async Task<IReadOnlyList<AgentGrant>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenSession();
        var grants = await session.LoadStartingWithAsync<AgentGrant>(AgentGrantIds.Prefix(userId), 0, 1024, cancellationToken: cancellationToken);

        // The prefix is the user's own; the check below is the second lock on the same door.
        return grants.Where(g => string.Equals(g.UserId, userId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The user's grant with that id, read now; <see langword="null"/> if it is not theirs.</summary>
    public async Task<AgentGrant?> GetOwnAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenSession();
        var grant = await session.LoadAsync<AgentGrant>(id, cancellationToken);
        return grant is not null && string.Equals(grant.UserId, userId, StringComparison.OrdinalIgnoreCase) ? grant : null;
    }

    /// <summary>Stores <paramref name="grant"/> (a new one, or an edited copy) and invalidates its cache entry.</summary>
    public async Task SaveAsync(AgentGrant grant, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenSession();
        await session.StoreAsync(grant, grant.Id.Value, cancellationToken);
        if (grant.ExpiresAt is { } expires)
        {
            // The store's expiry removes the document once the token cannot be used anyway (RavenDB's when the database
            // has expiration enabled; the token check refuses an expired token either way).
            session.SetExpiry(grant, expires);
        }
        await session.SaveChangesAsync(cancellationToken);
        Invalidate(grant.Id.Value);
    }

    /// <summary>
    /// Stores <paramref name="grant"/> only if no document has its id yet, and answers the grant that exists afterwards:
    /// <paramref name="grant"/>, or the one a concurrent first call (or the user) wrote first, which wins.
    /// </summary>
    public async Task<AgentGrant?> CreateIfMissingAsync(AgentGrant grant, CancellationToken cancellationToken = default)
    {
        try
        {
            using var session = store.OpenSession();
            // An empty expected version: the write fails if the document exists (optimistic concurrency on creation).
            await session.StoreAsync(grant, string.Empty, grant.Id.Value, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            Invalidate(grant.Id.Value);
            return grant;
        }
        catch (SparkStoreConcurrencyException)
        {
            Invalidate(grant.Id.Value);
            return await GetAsync(grant.Id.Value, cancellationToken);
        }
    }

    /// <summary>Deletes the grant: the agent's next call is refused. Answers whether it existed.</summary>
    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenSession();
        var grant = await session.LoadAsync<AgentGrant>(id, cancellationToken);
        if (grant is null || !string.Equals(grant.UserId, userId, StringComparison.OrdinalIgnoreCase))
            return false;

        session.Delete(grant.Id);
        await session.SaveChangesAsync(cancellationToken);
        Invalidate(id);
        return true;
    }

    /// <summary>Drops the cached read of <paramref name="id"/>.</summary>
    public void Invalidate(string id) => cache.TryRemove(id, out _);

    /// <summary>
    /// Records a call: <see cref="AgentGrant.LastUsedAt"/> is written at most once per <see cref="TouchEvery"/>
    /// per grant on this node (D15). Load-modify-save under optimistic concurrency (#536: no patch on every store), so it
    /// never overwrites an edit: a concurrent write wins and this touch is skipped.
    /// </summary>
    public async Task TouchAsync(AgentGrant grant, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var id = grant.Id.Value;
        var last = touched.TryGetValue(id, out var written) ? written : grant.LastUsedAt ?? DateTimeOffset.MinValue;
        if (now - last < TouchEvery)
            return;

        touched[id] = now;
        try
        {
            using var session = store.OpenSession();
            session.OptimisticConcurrency = true;
            if (await session.LoadAsync<AgentGrant>(id, cancellationToken) is not { } stored)
                return;
            stored.LastUsedAt = now;
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (SparkStoreConcurrencyException)
        {
            // Someone wrote the grant meanwhile: theirs stands, and the next call touches it again.
            touched.TryRemove(id, out _);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Bookkeeping: a failed write must not fail the agent's call.
            LogTouchFailed(logger, exception, id);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in cache)
        {
            if (now - entry.LoadedAt >= CacheFor)
                cache.TryRemove(key, out _);
        }
    }

    [LoggerMessage(EventId = 7301, Level = LogLevel.Warning, Message = "Could not record the last use of agent grant {GrantId}.")]
    private static partial void LogTouchFailed(ILogger logger, Exception exception, string grantId);
}
