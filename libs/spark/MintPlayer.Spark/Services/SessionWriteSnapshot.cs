using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// What the request session tracked before a write operation started, so a refused operation can
/// evict every document it (or an interceptor, or an Actions hook) stored, changed or deleted in
/// that session — not only its target (contributions F6).
/// </summary>
/// <remarks>
/// <para>
/// Why a snapshot of <see cref="IAdvancedDocumentSessionOperations.GetTrackedEntities"/> and not the
/// session's events: <c>OnBeforeStore</c>/<c>OnBeforeDelete</c> fire inside <c>SaveChangesAsync</c>,
/// which a refused operation never reaches, and nothing fires on <c>StoreAsync</c> or on mutating a
/// loaded entity. The tracked-entity list is the session's own record of what the next
/// <c>SaveChangesAsync</c> would write, so diffing it before and after is complete for entities,
/// whoever put them there.
/// </para>
/// <para>
/// What the diff evicts: an entity tracked only since the snapshot (stored, loaded or deleted during
/// the operation), an entity already tracked that was clean and is now changed, and one that was not
/// deleted and now is. What it leaves alone: changes that were already pending when the operation
/// started — they belong to whoever made them earlier in the request, not to the refused operation.
/// </para>
/// <para>
/// ⚠️ Not covered: a raw deferred command (<c>session.Advanced.Defer</c>), and <c>session.Delete(id)</c>
/// of a document the session never loaded, which RavenDB records as a deferred command rather than a
/// tracked entity. Neither can be withdrawn from a session.
/// </para>
/// </remarks>
internal sealed class SessionWriteSnapshot
{
    private readonly IAsyncDocumentSession session;
    private readonly HashSet<object> tracked = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> dirty = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> deleted = new(ReferenceEqualityComparer.Instance);

    private SessionWriteSnapshot(IAsyncDocumentSession session)
    {
        this.session = session;
        foreach (var info in session.Advanced.GetTrackedEntities().Values)
        {
            if (info.Entity is not { } entity)
                continue;
            tracked.Add(entity);
            if (info.IsDeleted)
                deleted.Add(entity);
            else if (session.Advanced.HasChanged(entity))
                dirty.Add(entity);
        }
    }

    /// <summary>Records what <paramref name="session"/> tracks right now.</summary>
    public static SessionWriteSnapshot Take(IAsyncDocumentSession session) => new(session);

    /// <summary>
    /// Evicts every entity the session started tracking, changed or deleted since the snapshot.
    /// Returns how many were evicted.
    /// </summary>
    public int EvictWrittenSince()
    {
        var evict = new List<object>();
        foreach (var info in session.Advanced.GetTrackedEntities().Values)
        {
            if (info.Entity is not { } entity)
                continue;

            if (!tracked.Contains(entity))
                evict.Add(entity);
            else if (info.IsDeleted)
            {
                if (!deleted.Contains(entity))
                    evict.Add(entity);
            }
            else if (!dirty.Contains(entity) && session.Advanced.HasChanged(entity))
                evict.Add(entity);
        }

        foreach (var entity in evict)
            session.Advanced.Evict(entity);
        return evict.Count;
    }
}
