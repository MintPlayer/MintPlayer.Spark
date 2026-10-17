using Raven.Client.Documents;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Http;

namespace CodeCoverage.Migrations;

/// <summary>
/// Runs a set-based RavenDB operation (patch or delete by query) and returns its
/// <see cref="BulkOperationResult"/>, never <c>null</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Why not <c>(await store.Operations.SendAsync(op)).WaitForCompletionAsync&lt;BulkOperationResult&gt;()</c>.</b>
/// That call can return <c>null</c> for an operation that completed. The RavenDB 7.2 server keeps
/// one mutable <c>OperationState</c> per operation. When the operation ends, its continuation writes
/// <c>Result</c> and then <c>Status = Completed</c> (<c>AbstractOperations.AddOperationInternalAsync</c>),
/// while <c>GET /operations/state</c> serializes the same object with <c>OperationState.ToJson()</c>,
/// which reads <c>Result</c> and then <c>Status</c>, without a lock. A state read that straddles
/// the two writes answers <c>Completed</c> with no result. The client's <c>Operation</c> fetches the
/// state once right after subscribing, and on <c>Completed</c> it hands back
/// <c>change.State.Result</c> unchecked.
/// </para>
/// <para>
/// A re-read cannot be torn: <c>Completed</c> was seen, so <c>Result</c> was written before it.
/// This helper therefore reads the state again when the wait answers <c>null</c>. It needs the
/// operation id, which <c>Operation</c> keeps internal, so it sends the command itself, exactly
/// as <c>OperationExecutor.SendAsync</c> does.
/// </para>
/// </remarks>
internal static class BulkOperation
{
    /// <summary>Creates the client-side handle that waits for operation <c>id</c>. A test seam.</summary>
    internal delegate Operation OperationFactory(
        RequestExecutor executor, Func<ISingleNodeDatabaseChanges> changes, DocumentConventions conventions, long id, string nodeTag);

    private static readonly OperationFactory DefaultFactory =
        (executor, changes, conventions, id, nodeTag) => new Operation(executor, changes, conventions, id, nodeTag);

    /// <summary>Sends <paramref name="operation"/> and waits for its result.</summary>
    public static Task<BulkOperationResult> RunAsync(
        IDocumentStore store, IOperation<OperationIdResult> operation, CancellationToken cancellationToken)
        => RunAsync(store, operation, DefaultFactory, cancellationToken);

    internal static async Task<BulkOperationResult> RunAsync(
        IDocumentStore store, IOperation<OperationIdResult> operation, OperationFactory createOperation, CancellationToken cancellationToken)
    {
        var executor = store.GetRequestExecutor();
        long id;
        string nodeTag;
        using (executor.ContextPool.AllocateOperationContext(out var context))
        {
            var command = operation.GetCommand(store, executor.Conventions, context, executor.Cache);
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);
            id = command.Result.OperationId;
            nodeTag = command.SelectedNodeTag ?? command.Result.OperationNodeTag;
        }

        var running = createOperation(executor, () => store.Changes(store.Database, nodeTag), executor.Conventions, id, nodeTag);
        var result = await running.WaitForCompletionAsync<BulkOperationResult>(cancellationToken);
        if (result is not null)
            return result;

        // Completed was observed with no result: the torn read. Read the state again.
        var state = await store.Maintenance.SendAsync(new GetOperationStateOperation(id, nodeTag), cancellationToken);
        return state is { Status: OperationStatus.Completed, Result: BulkOperationResult reread }
            ? reread
            : throw new InvalidOperationException(
                $"Operation {id} completed, but its state carries no bulk result (status {state?.Status.ToString() ?? "unknown"}, result {state?.Result?.GetType().Name ?? "none"}).");
    }
}
