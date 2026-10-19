namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Reacts to a replica of <typeparamref name="T"/> changing (warehouse §4.2, WD2): the durable way a consumer
/// derives its own state from another module's facts. The notification is enqueued in the same session that
/// applies the push, so a crash never loses one; it is delivered at least once, outside any request.
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
/// <param name="ReplicationVersion">The source's version stamp of <paramref name="Current"/>.</param>
public sealed record ReplicaChange<T>(string Id, string SourceModule, T? Previous, T? Current, long ReplicationVersion) where T : class
{
    public bool IsCreated => Previous is null && Current is not null;
    public bool IsDeleted => Current is null;
}

/// <summary>
/// For library code that writes a published source document with a raw session (bypassing Spark's save
/// pipeline, so no interceptor captures it): records the change in the same session, so the push commits
/// with the write (warehouse §4.2). A raw write nobody marks is still caught by the consumers' reconcile,
/// through the version stamp, but only on its next run.
/// </summary>
public interface IReplicaPublisher
{
    /// <param name="session">The RavenDB <c>IAsyncDocumentSession</c> the write commits through.</param>
    /// <param name="sourceType">The source document's CLR type (a <c>Publish&lt;TSource, …&gt;</c> source).</param>
    /// <param name="id">The document id; a deleted id is pushed as a tombstone.</param>
    Task MarkChangedAsync(object session, Type sourceType, string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Told when the owner refused a write-back request this module sent (F12), so the initiator hears about it —
/// typically by wiring it to the Notifications package's <c>ISparkNotifier</c>. Every refusal is also recorded on
/// the request document (<c>SparkSyncActions</c>, status Refused, with its reason) and logged.
/// </summary>
public interface ISyncActionRefusalObserver
{
    Task OnRefusedAsync(SyncActionRefusal refusal, CancellationToken cancellationToken);
}

/// <summary>A refused write-back request.</summary>
/// <param name="RequestId">The request document's id in this module's database.</param>
/// <param name="OwnerModule">The module that refused it.</param>
/// <param name="Contract">The contract of the replica.</param>
/// <param name="DocumentId">The document the edit was for.</param>
/// <param name="InitiatorId">The user who made the edit, when known.</param>
/// <param name="Reason">The owner's reason.</param>
/// <param name="Attributes">For an attribute-rights refusal, the fields the module may not write.</param>
public sealed record SyncActionRefusal(
    string RequestId,
    string OwnerModule,
    string Contract,
    string DocumentId,
    string? InitiatorId,
    string Reason,
    IReadOnlyList<string> Attributes);
