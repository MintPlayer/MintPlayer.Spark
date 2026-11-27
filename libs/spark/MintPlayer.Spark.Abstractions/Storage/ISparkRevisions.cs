namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The stored history of documents (#536 S8, PRD O12): every write to a collection that keeps revisions leaves a copy of
/// the whole document, read back by version. RavenDB keeps them itself (its revisions); the relational providers write
/// them to Spark's <c>spark.Revisions</c> table in the writing transaction; the in-memory store keeps them beside the
/// documents. A store offers it with <see cref="SparkStoreCapabilities.Revisions"/>.
/// </summary>
/// <remarks>
/// <para>
/// A collection keeps revisions only once <see cref="ConfigureAsync"/> enabled it (History does, from the model and
/// <c>Spark:History</c>). RavenDB also honours a configuration set outside Spark, such as its database-wide default.
/// </para>
/// <para>
/// Every write path writes them: unit-of-work saves, <c>PatchAppend</c>, <c>UpdateWhereAsync</c>,
/// <c>DeleteWhereAsync</c> and the expiry sweep. A delete leaves a revision without content
/// (<see cref="SparkRevisionInfo.IsDelete"/>), unless the collection purges on delete.
/// </para>
/// </remarks>
public interface ISparkRevisions
{
    /// <summary>
    /// Sets the revision settings of <paramref name="collections"/>. A collection not named keeps what it has, and
    /// nothing is written when every named one already matches. Returns the labels of the collections it changed.
    /// </summary>
    Task<IReadOnlyList<string>> ConfigureAsync(IReadOnlyList<SparkRevisionCollection> collections, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most a collection may ask for, or <see langword="null"/> when there is no cap. RavenDB's Community licence
    /// caps them (2 revisions, 45 days); the relational and in-memory stores have none.
    /// </summary>
    Task<SparkRevisionLimits?> GetLimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>The revisions of the document <paramref name="id"/>, newest first.</summary>
    Task<IReadOnlyList<SparkRevisionInfo>> ListAsync(Type entityType, string id, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// The revisions <paramref name="versions"/> of the document <paramref name="id"/>, as <paramref name="entityType"/>,
    /// by version. A version that is not a revision of THIS document in THIS type's collection, or a delete revision, is
    /// absent: a RavenDB change vector is not scoped to a document.
    /// </summary>
    Task<IReadOnlyDictionary<string, object>> GetAsync(Type entityType, string id, IReadOnlyCollection<string> versions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when this connection may not delete revisions (RavenDB: deleting them
    /// is a database-admin operation), so a purge can be refused before it deletes anything.
    /// </summary>
    Task EnsureCanDeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every revision of the document <paramref name="id"/>. Returns how many went.</summary>
    Task<long> DeleteAsync(Type entityType, string id, CancellationToken cancellationToken = default);
}

/// <summary>What one collection keeps. A limit of <see langword="null"/> is none.</summary>
/// <param name="Enabled">Whether writes to the collection leave revisions.</param>
/// <param name="MinimumRevisionsToKeep">At least this many revisions are kept per document; older ones may go.</param>
/// <param name="MinimumRevisionAgeToKeep">Revisions younger than this are kept.</param>
/// <param name="PurgeOnDelete">A (hard) delete takes the document's revisions with it, leaving no delete revision.</param>
/// <remarks>
/// A revision goes once it is beyond <paramref name="MinimumRevisionsToKeep"/> AND older than
/// <paramref name="MinimumRevisionAgeToKeep"/>; with one of them unset, the other decides alone (RavenDB's rule).
/// </remarks>
public sealed record SparkRevisionSettings(bool Enabled, long? MinimumRevisionsToKeep, TimeSpan? MinimumRevisionAgeToKeep, bool PurgeOnDelete);

/// <summary>A collection's wanted settings, with a label for messages (<c>"questions (Question)"</c>).</summary>
public sealed record SparkRevisionCollection(string Collection, string Label, SparkRevisionSettings Settings);

/// <summary>The caps a store puts on a collection's settings (<see cref="ISparkRevisions.GetLimitsAsync"/>).</summary>
public sealed record SparkRevisionLimits(string Name, long MaxRevisionsToKeep, TimeSpan MaxRevisionAgeToKeep);

/// <summary>One revision of a document.</summary>
/// <param name="Version">The document's version the revision holds: what <see cref="ISparkRevisions.GetAsync"/> takes, and
/// equal to the document's own version when the revision is its current state.</param>
/// <param name="LastModified">When it was written.</param>
/// <param name="IsDelete">A revision a delete left: it has no content.</param>
public sealed record SparkRevisionInfo(string Version, DateTimeOffset? LastModified, bool IsDelete);
