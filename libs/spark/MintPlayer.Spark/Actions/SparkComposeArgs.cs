using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// What <see cref="DefaultPersistentObjectActions{T}.OnComposeAsync"/> is handed: a freshly
/// scaffolded object for the type (every declared attribute present with full metadata and a null
/// value) and the id the caller requested.
/// <para>
/// This is the seam for pages that exist in the model but not in the database — a start page, a
/// dashboard, a per-user landing object. The handler fills attribute values and
/// <see cref="Abstractions.PersistentObject.Breadcrumb"/> (which the client renders as the page
/// title) and returns the object; it is free to ignore <see cref="RequestedId"/>.
/// </para>
/// </summary>
public sealed class SparkComposeArgs
{
    internal SparkComposeArgs(string requestedId, PersistentObject persistentObject)
    {
        RequestedId = requestedId;
        PersistentObject = persistentObject;
    }

    /// <summary>
    /// The id from the URL — for a program-unit deep link, whatever the unit's <c>objectId</c>
    /// declares. A composed page typically ignores it; a handler that resolves per-id content may
    /// use it, but must treat it as untrusted caller input.
    /// </summary>
    public string RequestedId { get; }

    /// <summary>
    /// The object to compose, scaffolded from the model. Its <see cref="Abstractions.PersistentObject.Id"/>
    /// is pre-set to <see cref="RequestedId"/> so the client's URL and the returned object agree.
    /// </summary>
    public PersistentObject PersistentObject { get; }
}
