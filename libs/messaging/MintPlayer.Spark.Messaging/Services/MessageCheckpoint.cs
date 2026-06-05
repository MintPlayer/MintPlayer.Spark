using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Scoped service that writes checkpoint data to the current HandlerExecution
/// on the SparkMessage document. The active handler execution is set by
/// MessageSubscriptionWorker before invoking each handler.
/// </summary>
internal sealed class MessageCheckpoint : IMessageCheckpoint
{
    private IAsyncDocumentSession? _session;
    private HandlerExecution? _handlerExecution;

    internal void SetContext(IAsyncDocumentSession session, HandlerExecution handlerExecution)
    {
        _session = session;
        _handlerExecution = handlerExecution;
    }

    public async Task SaveAsync(string checkpoint, CancellationToken cancellationToken = default)
    {
        if (_handlerExecution == null || _session == null)
            throw new InvalidOperationException("IMessageCheckpoint can only be used inside a message handler.");

        _handlerExecution.Checkpoint = checkpoint;
        await _session.SaveChangesAsync(cancellationToken);
    }
}
