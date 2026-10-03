using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The framework's durable after-commit interceptors (#482, D17), delivered by Messaging: one message per
/// committed row and interceptor, stored in the write's own session, handled with Messaging's retries and
/// dead-lettering.
/// </summary>
internal sealed partial class SparkAfterCommitOutbox : ISparkAfterCommitOutbox
{
    [Inject] private readonly IMessageOutbox outbox;

    public Task EnqueueAsync(object session, SparkAfterCommitWork work, CancellationToken cancellationToken = default)
        => outbox.EnqueueAsync((IAsyncDocumentSession)session, work, cancellationToken: cancellationToken);
}

/// <summary>Runs the durable interceptor a <see cref="SparkAfterCommitWork"/> message names; a throw is retried.</summary>
internal sealed partial class SparkAfterCommitRecipient : IRecipient<SparkAfterCommitWork>
{
    [Inject] private readonly ISparkAfterCommitDispatcher dispatcher;

    public async Task HandleAsync(SparkAfterCommitWork message, CancellationToken cancellationToken = default)
    {
        if (!await dispatcher.DispatchAsync(message, cancellationToken))
            throw new NonRetryableException(
                $"The after-commit interceptor '{message.InterceptorType}' is not registered in this app (removed since the write?); " +
                $"{message.Change.EntityType} '{message.Change.Id}' was committed without it.");
    }
}
