using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Abstractions;

public interface IManager
{
    /// <summary>
    /// Scaffolds a blank PersistentObject for the entity type registered under
    /// <paramref name="name"/>. All declared attributes are created with full
    /// metadata (DataType, Label, Rules, Renderer, ShowedOn, Order, Group,
    /// IsRequired/Visible/ReadOnly/Array, Query for References); Value is null.
    /// Throws <see cref="KeyNotFoundException"/> on unknown or ambiguous name —
    /// prefer the <see cref="GetPersistentObjectAsync(Guid, string, CancellationToken)"/> overload in apps that
    /// declare entities across multiple database schemas.
    /// </summary>
    /// <remarks>
    /// This is the idiomatic way to build a PO for a popup / dialog / form —
    /// declare the shape as a Virtual PO in <c>App_Data/Model/*.json</c> and
    /// look it up by name, rather than constructing the PO and its attributes
    /// by hand.
    /// <para>
    /// <b>Built for the current caller</b> (D13a): an attribute the caller may not read is absent, and
    /// one they may not edit (on <paramref name="verb"/> <c>New</c>: create) is read-only. Writing to an
    /// absent attribute through the indexer is a silent no-op; ask
    /// <see cref="PersistentObject.TryGetAttribute"/> to know whether it is there. Outside a request (a
    /// background job) the caller is the system and nothing is removed. Code that needs the whole object
    /// asks <see cref="AsSystem"/>, by name, so it can be reviewed.
    /// </para>
    /// </remarks>
    /// <param name="verb">
    /// <c>Read</c> (the default) or <c>Edit</c>: Read-denied removed, Edit-denied read-only. <c>New</c>:
    /// Read-denied removed, New-denied read-only. <c>Query</c>: Query-denied removed.
    /// </param>
    Task<PersistentObject> GetPersistentObjectAsync(
        string name, string verb = Authorization.SparkCoreActions.Read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scaffolds a blank PersistentObject by ObjectTypeId. Unambiguous — preferred
    /// over the name overload whenever the caller already has the Guid
    /// (e.g. from the source-generated <c>PersistentObjectIds</c> constants).
    /// Built for the current caller, as the name overload.
    /// </summary>
    Task<PersistentObject> GetPersistentObjectAsync(
        Guid id, string verb = Authorization.SparkCoreActions.Read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scaffolds a blank PersistentObject for <typeparamref name="T"/>, resolving the
    /// ObjectTypeId by looking up <c>typeof(T).FullName</c> against the registered
    /// EntityTypeDefinitions. The cleanest path when the caller has a typed entity
    /// class — no Guid plumbing, no string names. Built for the current caller, as the
    /// name overload.
    /// </summary>
    Task<PersistentObject> GetPersistentObjectAsync<T>(
        string verb = Authorization.SparkCoreActions.Read, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// The elevated construction: whole objects, nothing removed for the caller. For system work —
    /// sync, replication, values the caller is not shown but a computation needs. ⚠️ An object built
    /// here is not a presentation: returned to a client it throws in Development and is pruned in
    /// Production (D13a).
    /// </summary>
    ISystemManager AsSystem();

    /// <summary>
    /// Access to the Retry Action subsystem.
    /// </summary>
    IRetryAccessor Retry { get; }

    /// <summary>
    /// Access to the client-operations accumulator — non-blocking side-effects
    /// (Navigate, Notify, RefreshAttribute, RefreshQuery, DisableAction*) that the
    /// backend pushes and the frontend executes after the current action completes.
    /// See <c>docs/prd/PRD-ClientOperations.md</c>.
    /// </summary>
    IClientAccessor Client { get; }

    /// <summary>
    /// Gets a translated message for the current request culture, with placeholder substitution.
    /// Looks up the key in translations.json and calls string.Format with the provided parameters.
    /// </summary>
    string GetTranslatedMessage(string key, params object[] parameters);

    /// <summary>
    /// Gets a translated message for a specific language, with placeholder substitution.
    /// Looks up the key in translations.json and calls string.Format with the provided parameters.
    /// </summary>
    string GetMessage(string key, string language, params object[] parameters);
}
