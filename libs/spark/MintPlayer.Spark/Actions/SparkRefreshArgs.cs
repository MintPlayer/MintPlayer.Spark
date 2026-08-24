using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// What <see cref="DefaultPersistentObjectActions{T}.OnRefreshAsync"/> is handed: the in-progress
/// object as the user currently has it on screen, and the attribute whose change asked for the
/// refresh.
/// <para>
/// The object is <b>not</b> the persisted entity and may never become one — it is a freshly
/// scaffolded object carrying the client's values, built without touching the database. Mutating it
/// changes what the form renders; it does not write anything.
/// </para>
/// </summary>
/// <typeparam name="T">The entity type the actions class serves.</typeparam>
public sealed class SparkRefreshArgs<T> where T : class
{
    internal SparkRefreshArgs(
        PersistentObject persistentObject,
        PersistentObjectAttribute? attribute,
        bool isNew,
        CancellationToken cancellationToken)
    {
        PersistentObject = persistentObject;
        Attribute = attribute;
        IsNew = isNew;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// The object being reshaped. Every attribute the model declares is present, carrying the value
    /// the client currently holds.
    /// </summary>
    public PersistentObject PersistentObject { get; }

    /// <summary>
    /// The attribute whose change triggered this refresh, or <see langword="null"/> when the client
    /// named an attribute the model does not declare.
    /// <para>
    /// Nullable rather than absent on purpose. Vidyano resolves the same thing lazily and hands
    /// handlers a <c>null</c> they cannot see coming, so every handler that reads
    /// <c>args.Attribute.Name</c> is one stale client away from a <see cref="NullReferenceException"/>.
    /// Making it nullable moves that from a runtime surprise to a compile-time one.
    /// </para>
    /// </summary>
    public PersistentObjectAttribute? Attribute { get; }

    /// <summary>
    /// Whether this is a new object with no persisted counterpart. A handler that loads the entity
    /// to make a decision must branch on this first.
    /// </summary>
    public bool IsNew { get; }

    /// <summary>Cancelled when the caller gives up on the request.</summary>
    public CancellationToken CancellationToken { get; }
}
