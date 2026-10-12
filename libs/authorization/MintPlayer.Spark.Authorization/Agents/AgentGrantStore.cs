using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Raven.Client;
using Raven.Client.Documents;

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
internal sealed partial class AgentGrantStore(IDocumentStore store, TimeProvider time, ILogger<AgentGrantStore> logger)
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

        using var session = store.OpenAsyncSession();
        var grant = await session.LoadAsync<AgentGrant>(id, cancellationToken);
        cache[id] = (grant, now);

        if (cache.Count > 10_000)
            Prune(now);
        return grant;
    }

    /// <summary>Every grant of <paramref name="userId"/>, read now (no cache).</summary>
    public async Task<IReadOnlyList<AgentGrant>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var grants = await session.Advanced.LoadStartingWithAsync<AgentGrant>(AgentGrantIds.Prefix(userId), pageSize: 1024, token: cancellationToken);

        // The prefix is the user's own; the check below is the second lock on the same door.
        return grants.Where(g => string.Equals(g.UserId, userId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The user's grant with that id, read now; <see langword="null"/> if it is not theirs.</summary>
    public async Task<AgentGrant?> GetOwnAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var grant = await session.LoadAsync<AgentGrant>(id, cancellationToken);
        return grant is not null && string.Equals(grant.UserId, userId, StringComparison.OrdinalIgnoreCase) ? grant : null;
    }

    /// <summary>Stores <paramref name="grant"/> (a new one, or an edited copy) and invalidates its cache entry.</summary>
    public async Task SaveAsync(AgentGrant grant, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(grant, grant.Id, cancellationToken);
        if (grant.ExpiresAt is { } expires)
        {
            // RavenDB's expiration removes the document once the token cannot be used anyway (when the database
            // has expiration enabled; the token check refuses an expired token either way).
            session.Advanced.GetMetadataFor(grant)[Constants.Documents.Metadata.Expires] = expires.UtcDateTime.ToString("O");
        }
        await session.SaveChangesAsync(cancellationToken);
        Invalidate(grant.Id!);
    }

    /// <summary>
    /// Stores <paramref name="grant"/> only if no document has its id yet, and answers the grant that exists afterwards:
    /// <paramref name="grant"/>, or the one a concurrent first call (or the user) wrote first, which wins.
    /// </summary>
    public async Task<AgentGrant?> CreateIfMissingAsync(AgentGrant grant, CancellationToken cancellationToken = default)
    {
        try
        {
            using var session = store.OpenAsyncSession();
            // An empty change vector: the write fails if the document exists (optimistic concurrency on creation).
            await session.StoreAsync(grant, string.Empty, grant.Id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            Invalidate(grant.Id!);
            return grant;
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            Invalidate(grant.Id!);
            return await GetAsync(grant.Id!, cancellationToken);
        }
    }

    /// <summary>Deletes the grant: the agent's next call is refused. Answers whether it existed.</summary>
    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var grant = await session.LoadAsync<AgentGrant>(id, cancellationToken);
        if (grant is null || !string.Equals(grant.UserId, userId, StringComparison.OrdinalIgnoreCase))
            return false;

        session.Delete(grant);
        await session.SaveChangesAsync(cancellationToken);
        Invalidate(id);
        return true;
    }

    /// <summary>Drops the cached read of <paramref name="id"/>.</summary>
    public void Invalidate(string id) => cache.TryRemove(id, out _);

    /// <summary>
    /// Records a call: <see cref="AgentGrant.LastUsedAt"/> is written at most once per <see cref="TouchEvery"/>
    /// per grant on this node (D15), as a single-field patch that never overwrites an edit.
    /// </summary>
    public async Task TouchAsync(AgentGrant grant, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var id = grant.Id!;
        var last = touched.TryGetValue(id, out var written) ? written : grant.LastUsedAt ?? DateTimeOffset.MinValue;
        if (now - last < TouchEvery)
            return;

        touched[id] = now;
        try
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.Patch<AgentGrant, DateTimeOffset?>(id, g => g.LastUsedAt, now);
            await session.SaveChangesAsync(cancellationToken);
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
