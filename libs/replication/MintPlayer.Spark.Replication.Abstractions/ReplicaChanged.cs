namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Reacts to a replica of <typeparamref name="T"/> changing (warehouse §4.2, WD2): the durable way a consumer
/// derives its own state from another module's facts. The notification is enqueued in the same session that
/// applies the push, so a crash never loses one; it is delivered at least once, outside any request. HTTP transport
/// only: an ETL load writes into the database directly and raises none.
/// <para>
/// It carries the previous and the new state. Derive transitions by comparing the two, never by assuming
/// an order of pushes: "packed" may arrive after "shipped", and the handler must then do nothing (idempotent,
/// order-insensitive). Register implementations in DI (scoped).
/// </para>
/// </summary>
public interface IReplicaChanged<T> where T : class
{
    Task OnReplicaChangedAsync(ReplicaChange<T> change, CancellationToken cancellationToken);
}

/// <summary>One applied change of a replica.</summary>
/// <param name="Id">The document id.</param>
/// <param name="SourceModule">The module the replica came from.</param>
/// <param name="Previous">The state before, or <see langword="null"/> when the replica was created.</param>
/// <param name="Current">The state now, or <see langword="null"/> when the source deleted it.</param>
/// <param name="SourceChangeVector">The source's change vector of <paramref name="Current"/>.</param>
public sealed record ReplicaChange<T>(string Id, string SourceModule, T? Previous, T? Current, string? SourceChangeVector) where T : class
{
    public bool IsCreated => Previous is null && Current is not null;

    public bool IsDeleted => Current is null;
}

/// <summary>
/// For library code that writes a replicated source document with a raw session (bypassing Spark's save pipeline,
/// so no interceptor captures it): records the change in the same session, so the push commits with the write
/// (warehouse §4.2.3). A raw write nobody marks is still caught by the owner's reconcile, on its next run.
/// </summary>
public interface IReplicaPublisher
{
    /// <param name="session">The RavenDB <c>IAsyncDocumentSession</c> the write commits through.</param>
    /// <param name="sourceType">The source document's CLR type.</param>
    /// <param name="id">The document id; a deleted id is pushed as a tombstone.</param>
    Task MarkChangedAsync(object session, Type sourceType, string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Told when a write-back this module sent ended for good without being applied (§4.2.5): the owner refused it, it
/// conflicted, or it gave up after the retry budget. The library already records the outcome for the initiator
/// (their "My pending changes"), pushes it to their open pages, and mails a give-up when mail is configured; an
/// observer adds anything else (a Notifications entry, a chat message). Register implementations in DI (scoped).
/// </summary>
public interface ISyncActionOutcomeObserver
{
    Task OnSyncActionEndedAsync(SyncActionEnded outcome, CancellationToken cancellationToken);
}

/// <summary>A write-back that ended without being applied.</summary>
/// <param name="SyncActionId">The <c>SparkSyncActions/…</c> document in this module's database.</param>
/// <param name="OwnerModule">The owner module.</param>
/// <param name="TargetCollection">The replica collection.</param>
/// <param name="DocumentId">The document the edit was for.</param>
/// <param name="InitiatorId">The user who made the edit, when known.</param>
/// <param name="Status"><c>Conflict</c>, <c>Refused</c> or <c>GaveUp</c>.</param>
/// <param name="Reason">The owner's reason, or why it gave up.</param>
/// <param name="Attributes">The conflicting, unwritable or invalid properties.</param>
public sealed record SyncActionEnded(
    string SyncActionId,
    string OwnerModule,
    string TargetCollection,
    string DocumentId,
    string? InitiatorId,
    string Status,
    string? Reason,
    IReadOnlyList<string> Attributes);
