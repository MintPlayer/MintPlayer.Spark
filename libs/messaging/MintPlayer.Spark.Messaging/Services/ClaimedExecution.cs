using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Runs a claimed message under the two guards every delivery path needs: the claim is renewed while
/// the handlers run, and the run is bounded by <see cref="SparkMessagingOptions.HandlerTimeout"/>.
/// <para>
/// Extracted from <see cref="MessageQueueRouter"/> so <see cref="MessageSubscriptionWorker"/>
/// (<see cref="ESubscriptionMode.SubscriptionPerQueue"/>) gets the same guards. It had neither: a
/// handler slower than <see cref="SparkMessagingOptions.ClaimTtl"/> was reclaimed by the sweeper
/// underneath itself and handled twice, and a handler that hung held its queue's subscription for
/// ever.
/// </para>
/// </summary>
internal static class ClaimedExecution
{
    public static async Task RunAsync(
        IDocumentStore store,
        SparkMessagingOptions options,
        ILogger logger,
        string messageId,
        Func<CancellationToken, Task> process,
        CancellationToken cancellationToken)
    {
        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewUntilDoneAsync(store, options, logger, messageId, renewalCts.Token);

        // Bounds the lane. A handler that throws is parked by MessageProcessor and the pump moves
        // on, so failures never hold the head of the queue — but a handler that HANGS would hold it
        // for ever: one message is in flight at a time, and the claim is renewed while it runs, so
        // the sweeper's reclaim never fires either. This is the only thing that ends that.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.HandlerTimeout);

        try
        {
            await process(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timed out rather than shut down. MessageProcessor's own catch will have parked the
            // message if it got that far; if the cancellation unwound past it, the claim lapses and
            // the sweeper reclaims it. Either way the message is not lost and the lane is freed.
            logger.LogError(
                "Message {MessageId} exceeded SparkMessagingOptions.HandlerTimeout ({Timeout}) and was "
                + "cancelled to free its queue. If this recurs, either the handler needs a longer "
                + "timeout or it is not honouring its CancellationToken.",
                messageId, options.HandlerTimeout);
        }
        finally
        {
            await renewalCts.CancelAsync();
            try { await renewal; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    private static async Task RenewUntilDoneAsync(
        IDocumentStore store, SparkMessagingOptions options, ILogger logger, string messageId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.ClaimRenewInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool stillOurs;
            try
            {
                stillOurs = await MessageClaims.TryRenewAsync(
                    store, messageId, MessageClaims.NodeId, options.ClaimTtl, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A transient failure to renew is not a lost claim: try again next interval, while
                // the TTL still has most of its length to run. Letting it escape faulted this task,
                // which ended renewal for the rest of the handler's run and then rethrew from the
                // `finally` in RunAsync — replacing the processing outcome, so a message that had
                // been handled successfully was logged as a pump failure.
                logger.LogWarning(ex,
                    "Could not renew the claim on message {MessageId}; retrying in {Interval}",
                    messageId, options.ClaimRenewInterval);
                continue;
            }

            if (!stillOurs)
            {
                // A renewal that lands just after the processor saved the outcome also fails; that
                // is the normal end of processing, and warning about it would be a false alarm.
                try
                {
                    if (!await MessageClaims.WasReclaimedAsync(store, messageId, MessageClaims.NodeId, cancellationToken))
                        return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Could not tell. Report the loss: a spurious warning costs less than hiding a
                    // real double-processing window.
                }

                // The claim was reclaimed while we were working. We cannot un-run the handlers
                // already invoked, but we can say so loudly: this is the window in which a message
                // can be processed twice, and it means ClaimTtl is too short for this handler.
                logger.LogWarning(
                    "Lost the claim on message {MessageId} while still processing it — it has been "
                    + "requeued and may be handled twice. Increase SparkMessagingOptions.ClaimTtl.",
                    messageId);
                return;
            }
        }
    }
}
