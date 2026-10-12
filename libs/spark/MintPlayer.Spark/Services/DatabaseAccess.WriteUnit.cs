using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Several writes as one: the checks of a save or a delete without the write, and a batch of saves and deletes
/// that commits once (MCP PRD D8, <c>propose_changes</c> / <c>apply_changes</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The guarantee of a batch</b> is RavenDB's for one <c>SaveChangesAsync</c>: every document the batch stores or
/// deletes goes to the server in one request, which the node that takes it applies as one transaction, all or
/// nothing. Each write carries the change vector it expects (the caller's etag), so a document changed by anyone
/// else since that version fails the whole request with a concurrency error and nothing is written. It is a
/// single-node transaction, as every Spark save is: not a cluster-wide one.
/// </para>
/// <para>
/// <b>What it does not cover:</b> a write an Actions class or an interceptor commits itself (its own
/// <c>SaveChangesAsync</c>, or a separate session) is outside the batch. Framework paths that commit on their
/// own (bulk delete, restore, purge) refuse to run inside a batch rather than commit early.
/// </para>
/// </remarks>
internal interface ISparkWriteUnit
{
    /// <summary>
    /// Every gate and the validation of saving <paramref name="persistentObject"/>, exactly as the save runs
    /// them, and no write. Throws what the save would throw. The object is shielded in place: an attribute
    /// the caller may not write carries the stored value (or the default) afterwards, as it would be saved.
    /// </summary>
    Task CheckSaveAsync(PersistentObject persistentObject);

    /// <summary>
    /// Every gate of deleting <paramref name="id"/> at version <paramref name="etag"/>, and no delete.
    /// <see langword="false"/> when there is no such row of the type (the delete would do nothing).
    /// </summary>
    Task<bool> CheckDeleteAsync(Guid objectTypeId, string id, string etag);

    /// <summary>
    /// Runs <paramref name="operations"/> — saves and deletes through <see cref="IDatabaseAccess"/> — and commits
    /// them in one <c>SaveChangesAsync</c>. After-save and after-delete interceptors run after that commit. On any
    /// exception nothing is committed, the request session is cleared (so no part of the batch can reach a later
    /// commit), and the exception propagates; a RavenDB concurrency error becomes
    /// <see cref="SparkConcurrencyException"/>.
    /// </summary>
    /// <param name="requestBudget">The request count the batch is expected to stay under (logged when exceeded).</param>
    /// <param name="label">What the batch is, for that log line.</param>
    Task RunInOneCommitAsync(Func<Task> operations, int requestBudget, string label);
}

internal partial class DatabaseAccess : ISparkWriteUnit
{
    /// <summary>The open write batch of this request, or <see langword="null"/>.</summary>
    private WriteBatch? batch;

    internal sealed class WriteBatch
    {
        public List<Func<Task>> AfterCommit { get; } = [];

        /// <summary>Set while the batch itself commits, the one commit allowed while it is open.</summary>
        public bool Committing { get; set; }
    }

    public Task CheckSaveAsync(PersistentObject persistentObject)
        => SaveCoreAsync(persistentObject, PersistentObjectOperation.Save, checkOnly: true);

    public Task<bool> CheckDeleteAsync(Guid objectTypeId, string id, string etag)
        => DeleteCoreAsync(objectTypeId, id, PersistentObjectOperation.Delete, etag, checkOnly: true);

    public async Task RunInOneCommitAsync(Func<Task> operations, int requestBudget, string label)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (batch is not null)
            throw new InvalidOperationException("A write batch is already open in this request; batches do not nest.");

        var current = new WriteBatch();
        batch = current;
        try
        {
            using (session.IgnoreMaxRequests(requestBudget, logger, label))
            {
                await operations();
                current.Committing = true;
                await CommitAsync();
            }
        }
        catch (Exception ex)
        {
            // Nothing of the batch may reach a later SaveChangesAsync in this request. Clear, not the per-operation
            // snapshot eviction: a delete is recorded as a deferred command, which a session cannot withdraw on its
            // own (SessionWriteSnapshot's caveat), and the batch owns the request's writes while it is open.
            session.Advanced.Clear();
            if (ex is Raven.Client.Exceptions.ConcurrencyException)
                throw new SparkConcurrencyException(ex);
            throw;
        }
        finally
        {
            batch = null;
        }

        foreach (var work in current.AfterCommit)
            await work();
    }

    /// <summary>A framework path that commits by itself, asked to run inside a batch: refused, never an early commit.</summary>
    private void EnsureNotInsideBatch()
    {
        if (batch is { Committing: false })
            throw new InvalidOperationException(
                "This write commits on its own and cannot join a write batch (ISparkWriteUnit): only saves and deletes through IDatabaseAccess do.");
    }
}
