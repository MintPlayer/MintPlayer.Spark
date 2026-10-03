namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>
/// A durable interceptor that runs after a save committed (#482, D17). The framework stores one outbox
/// message per row and interceptor <b>in the save's own commit</b>, and Spark Messaging delivers it with
/// retries and dead-lettering: once the write is committed, the interceptor eventually runs, even across a
/// crash right after the commit. Registering one without Messaging is a startup error.
/// </summary>
/// <remarks>
/// <para>
/// It gets a <see cref="SparkCommittedChange"/>, never the live entity: the row may have changed
/// again by the time the message is handled. Load it when the current state matters. What an interceptor
/// needs to know about the write itself, a before-interceptor records in <see cref="SparkInterceptorContext.Facts"/>.
/// </para>
/// <para>
/// It runs outside any request, in its own DI scope, at least once: make it idempotent. Throw to
/// retry. It is the place for work that must not be lost (notifications, reversals, cleanup); work
/// that must happen before the response, or that handles a secret, stays an <see cref="IAfterSave"/>.
/// </para>
/// </remarks>
public interface IAfterSaveCommitted : ISparkInterceptor
{
    Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IAfterSaveCommitted"/> for <typeparamref name="T"/> and its subtypes.</summary>
public interface IAfterSaveCommitted<T> : IAfterSaveCommitted, ISparkInterceptor<T> where T : class;

/// <summary>
/// A durable interceptor that runs after a delete committed — hard, replaced (a soft delete) or a purge.
/// Same delivery as <see cref="IAfterSaveCommitted"/>.
/// </summary>
public interface IAfterDeleteCommitted : ISparkInterceptor
{
    Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IAfterDeleteCommitted"/> for <typeparamref name="T"/> and its subtypes.</summary>
public interface IAfterDeleteCommitted<T> : IAfterDeleteCommitted, ISparkInterceptor<T> where T : class;

/// <summary>What a durable after-commit interceptor is told: a committed write, as data.</summary>
/// <remarks>
/// The new change vector is not here: the message is written in the same commit as the change, before
/// the database assigns it. An interceptor that needs it loads the row.
/// </remarks>
public sealed record SparkCommittedChange
{
    /// <summary>The CLR entity type's full name.</summary>
    public required string EntityType { get; init; }

    /// <summary>The document id.</summary>
    public required string Id { get; init; }

    /// <summary>What kind of write: New, Save, Revert, Restore, Sync, Delete or Purge.</summary>
    public required PersistentObjectOperation Operation { get; init; }

    /// <summary>Whether an <see cref="IDeleteReplacement"/> turned the delete into a save (a soft delete).</summary>
    public bool IsReplaced { get; init; }

    /// <summary>Whether the delete was a purge.</summary>
    public bool IsPurge => Operation == PersistentObjectOperation.Purge;

    /// <summary>Whether the save created the row.</summary>
    public bool IsNew => Operation == PersistentObjectOperation.New;

    /// <summary>The acting user's id; null for an anonymous caller or the system.</summary>
    public string? UserId { get; init; }

    /// <summary>Whether the write ran in the system context.</summary>
    public bool IsSystemContext { get; init; }

    /// <summary>When the framework wrote it.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The change vector the row had before this write; null for a create.</summary>
    public string? PreviousChangeVector { get; init; }

    /// <summary>What the before-interceptors recorded in <see cref="SparkInterceptorContext.Facts"/>.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    /// <summary>The reason given for a delete (<see cref="SparkFacts.Reason"/>), if any.</summary>
    public string? Reason => Facts.TryGetValue(SparkFacts.Reason, out var reason) ? reason : null;
}

/// <summary>Fact keys the framework and its libraries share.</summary>
public static class SparkFacts
{
    /// <summary>The reason given for a delete: the caller's, or the one SoftDelete stored.</summary>
    public const string Reason = "Reason";

    /// <summary>The top-level attributes an edit changed, comma-separated (filled by History for the types it governs).</summary>
    public const string ChangedAttributes = "ChangedAttributes";
}

/// <summary>
/// The seam the framework writes durable after-commit work through (#482, D17). Spark Messaging
/// implements it; without it, a registered <see cref="IAfterSaveCommitted"/> or
/// <see cref="IAfterDeleteCommitted"/> is a startup error.
/// </summary>
public interface ISparkAfterCommitOutbox
{
    /// <summary>
    /// Stores one message for <paramref name="interceptorType"/> in <paramref name="session"/> (the RavenDB
    /// <c>IAsyncDocumentSession</c> the write commits through), so it commits — or is taken back —
    /// with the write. Only stores: the caller saves.
    /// </summary>
    Task EnqueueAsync(object session, SparkAfterCommitWork work, CancellationToken cancellationToken = default);
}

/// <summary>One durable interceptor to run for one committed row.</summary>
public sealed record SparkAfterCommitWork
{
    /// <summary>The interceptor's full type name; resolved only among the interceptors the app registered.</summary>
    public required string InterceptorType { get; init; }

    /// <summary>Whether the change is a delete (<see cref="IAfterDeleteCommitted"/>) rather than a save.</summary>
    public required bool IsDelete { get; init; }

    public required SparkCommittedChange Change { get; init; }
}

/// <summary>
/// Runs one unit of durable after-commit work — what the outbox's message handler calls. Implemented
/// by the framework.
/// </summary>
public interface ISparkAfterCommitDispatcher
{
    /// <returns>False when the interceptor no longer exists in this app (removed since the write).</returns>
    Task<bool> DispatchAsync(SparkAfterCommitWork work, CancellationToken cancellationToken);
}
