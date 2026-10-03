using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// Refuses a hard delete of an <see cref="ISoftDeletable"/> document that did not come through the
/// framework (#467, D32): a raw <c>session.Delete</c> would skip the replacement, the purge's revision
/// wipe and every hook. The framework's own deletes (a purge, a type's hard delete) are allowed, and
/// so is code inside <see cref="SparkRawWrites.Allow"/> (a migration, a test fixture).
/// </summary>
/// <remarks>
/// RavenDB raises <c>OnBeforeDelete</c> for every session delete form — of a tracked entity, and by id
/// of a tracked or untracked document, with or without a change vector (measured,
/// <c>Issue467RawDeleteEventSpikeTests</c>). Patches and delete-by-query raise nothing: a guard, not a wall.
/// A delete by id names no entity, so it is matched by collection prefix
/// (<c>{Collection}{separator}</c>); a document stored under an id outside that convention is not recognised.
/// </remarks>
internal static class SoftDeleteRawDeleteGuard
{
    public static void Install(IDocumentStore store, IReadOnlyCollection<Type> softDeletable)
    {
        if (softDeletable.Count == 0 || store is not DocumentStoreBase storeBase)
            return;

        var separator = store.Conventions.IdentityPartsSeparator;
        var prefixes = softDeletable
            .Select(type => store.Conventions.GetCollectionName(type) + separator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        storeBase.OnBeforeDelete += (_, e) =>
        {
            var governed = e.Entity is not null
                ? e.Entity is ISoftDeletable
                : prefixes.Any(prefix => e.DocumentId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!governed || SparkRawWrites.IsAllowed(e.Session, e.DocumentId))
                return;

            throw new InvalidOperationException(
                $"'{e.DocumentId}' is soft-deletable, and a raw session delete bypasses SoftDelete. Delete it through " +
                "IDatabaseAccess (a Purge to delete it permanently), or wrap the SaveChangesAsync in SparkRawWrites.Allow() " +
                "when bypassing it is intended (a migration, a test fixture).");
        };
    }
}
