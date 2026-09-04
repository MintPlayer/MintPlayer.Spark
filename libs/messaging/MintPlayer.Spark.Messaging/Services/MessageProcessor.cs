using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Newtonsoft.Json;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Runs one message's handlers and records the outcome on the document.
/// </summary>
/// <remarks>
/// <para>
/// Split out of the old subscription worker so that the component deciding <i>which</i> message runs
/// next (<see cref="MessageLanePump"/>) is separate from the one that runs it. The pump owns
/// ordering; this owns the handler contract.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> Failure state lives in the document, never in an exception escaping to
/// the caller: a message that fails is recorded as failed and its lane keeps running. This is
/// load-bearing — an exception reaching RavenDB's subscription callback kills the worker without
/// self-healing, and progress is per batch rather than per document, so one poisoned message would
/// block every lane forever.
/// </para>
/// </remarks>
internal sealed class MessageProcessor(
    IDocumentStore store,
    IServiceProvider serviceProvider,
    IOptions<SparkMessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageProcessor> logger)
{
    private readonly SparkMessagingOptions options = options.Value;

    /// <summary>What the pump needs to know once a message has been attempted.</summary>
    /// <param name="Terminal">The message will never run again; its partition is free.</param>
    /// <param name="NextAttemptAtUtc">When it should next be attempted, if not terminal.</param>
    internal readonly record struct Outcome(bool Terminal, DateTime? NextAttemptAtUtc);

    public async Task<Outcome> ProcessAsync(string messageId, IRetrySchedule schedule, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(messageId, cancellationToken);

        if (message is null)
        {
            // Deleted underneath us — retention, or an operator. Nothing to do, and the partition
            // must not stay blocked on a document that no longer exists.
            logger.LogDebug("Message {MessageId} no longer exists; treating as terminal", messageId);
            return new Outcome(Terminal: true, NextAttemptAtUtc: null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            message.Status = EMessageStatus.Processing;
            message.AttemptCount++;

            // R2-H6: allow-list BEFORE Type.GetType. The allow-list holds only types with a
            // registered IRecipient<>, so someone who can write into SparkMessages cannot route
            // through Type.GetType to instantiate arbitrary types.
            //
            // The protection is that the type is never RESOLVED; the status recorded afterwards is a
            // separate question. Nobody subscribing is not a failure — publishing to zero subscribers
            // is a successful publish — so this completes rather than dead-letters. That keeps
            // dead-letter meaning "we tried and failed, a human may need to act", which matters
            // because a framework lane like spark-github-all broadcasts typed messages most
            // applications never subscribe to; dead-lettering them would bury real faults in noise.
            // It is logged as a warning, because it can equally mean a handler was removed while its
            // messages were still in flight.
            var allowList = serviceProvider.GetRequiredService<IMessageTypeAllowList>();
            if (!allowList.IsAllowedMessageType(message.MessageType))
            {
                logger.LogWarning(
                    "No recipient is registered for {MessageType}; completing {MessageId} with no handlers",
                    message.MessageType, message.Id);

                message.Status = EMessageStatus.Completed;
                message.CompletedAtUtc = now;
                SetExpiration(session, message, now);
                await session.SaveChangesAsync(cancellationToken);
                return new Outcome(Terminal: true, null);
            }

            var clrType = Type.GetType(message.MessageType);
            if (clrType == null)
            {
                return await DeadLetterMessageAsync(
                    session, message, now, $"Cannot resolve type {message.MessageType}", cancellationToken);
            }

            var payload = JsonConvert.DeserializeObject(message.PayloadJson, clrType);
            if (payload == null)
            {
                return await DeadLetterMessageAsync(
                    session, message, now, "Failed to deserialize payload", cancellationToken);
            }

            var recipientInterfaceType = ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
                ("MessageProcessor.RecipientInterface", clrType),
                static k => typeof(IRecipient<>).MakeGenericType(k.Type));
            var checkpointInterfaceType = ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
                ("MessageProcessor.CheckpointRecipientInterface", clrType),
                static k => typeof(ICheckpointRecipient<>).MakeGenericType(k.Type));

            using (var scope = serviceProvider.CreateScope())
            {
                if (message.Handlers.Count == 0)
                {
                    foreach (var recipient in scope.ServiceProvider.GetServices(recipientInterfaceType))
                    {
                        message.Handlers.Add(new HandlerExecution
                        {
                            HandlerType = recipient!.GetType().AssemblyQualifiedName!,
                            Status = EHandlerStatus.Pending,
                        });
                    }

                    if (message.Handlers.Count == 0)
                        logger.LogWarning("No recipients registered for {MessageType}, marking completed", clrType.FullName);

                    await session.SaveChangesAsync(cancellationToken);
                }

                var checkpoint = scope.ServiceProvider.GetService<IMessageCheckpoint>() as MessageCheckpoint;

                foreach (var handler in message.Handlers)
                {
                    // The guarantee: a retry re-runs ONLY what failed. A handler that already
                    // succeeded is never invoked a second time.
                    if (handler.Status is EHandlerStatus.Completed or EHandlerStatus.DeadLettered)
                        continue;

                    // R2-H6 again: HandlerType was captured from a registered recipient, but the
                    // document on disk is mutable.
                    if (!allowList.IsAllowedHandlerType(handler.HandlerType))
                    {
                        await DeadLetterHandlerAsync(session, message, handler,
                            $"Handler type not in allow-list: {handler.HandlerType}", cancellationToken);
                        continue;
                    }

                    var handlerType = Type.GetType(handler.HandlerType);
                    if (handlerType == null)
                    {
                        await DeadLetterHandlerAsync(session, message, handler,
                            $"Cannot resolve handler type: {handler.HandlerType}", cancellationToken);
                        continue;
                    }

                    var recipientInstance = scope.ServiceProvider.GetServices(recipientInterfaceType)
                        .FirstOrDefault(r => r!.GetType() == handlerType);

                    if (recipientInstance == null)
                    {
                        await DeadLetterHandlerAsync(session, message, handler,
                            $"Handler not found in DI: {handlerType.Name}", cancellationToken);
                        continue;
                    }

                    checkpoint?.SetContext(session, handler);

                    try
                    {
                        if (handler.Checkpoint != null && checkpointInterfaceType.IsAssignableFrom(handlerType))
                        {
                            var checkpointInterface = checkpointInterfaceType;
                            var msgType = clrType;
                            var checkpointHandleMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
                                ("MessageProcessor.CheckpointHandleAsync", clrType),
                                _ => checkpointInterface.GetMethod(
                                    nameof(ICheckpointRecipient<object>.HandleAsync),
                                    [msgType, typeof(string), typeof(CancellationToken)]));
                            await (Task)checkpointHandleMethod!.Invoke(recipientInstance, [payload, handler.Checkpoint, cancellationToken])!;
                        }
                        else
                        {
                            var recipientInterface = recipientInterfaceType;
                            var handleMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
                                ("MessageProcessor.RecipientHandleAsync", clrType),
                                _ => recipientInterface.GetMethod(nameof(IRecipient<object>.HandleAsync)));
                            await (Task)handleMethod!.Invoke(recipientInstance, [payload, cancellationToken])!;
                        }

                        handler.Status = EHandlerStatus.Completed;
                        handler.CompletedAtUtc = now;
                        await session.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception ex) when (IsNonRetryable(ex is TargetInvocationException tie ? tie.InnerException! : ex))
                    {
                        var actual = ex is TargetInvocationException t ? t.InnerException! : ex;
                        logger.LogWarning(actual, "Non-retryable error in {HandlerType} for {MessageId}, dead-lettering handler",
                            handlerType.Name, message.Id);
                        await DeadLetterHandlerAsync(session, message, handler, actual.Message, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var actual = ex is TargetInvocationException t ? t.InnerException! : ex;
                        logger.LogError(actual, "Error in {HandlerType} for {MessageId}", handlerType.Name, message.Id);

                        handler.AttemptCount++;
                        handler.LastError = actual.Message;

                        // One decision point: the schedule says whether there is another attempt,
                        // rather than three call sites each comparing a counter to a limit.
                        handler.Status = schedule.Next(handler.AttemptCount) is RetryDecision.DeadLetter
                            ? EHandlerStatus.DeadLettered
                            : EHandlerStatus.Failed;

                        if (handler.Status == EHandlerStatus.DeadLettered)
                        {
                            logger.LogWarning("Handler {HandlerType} dead-lettered after {AttemptCount} attempts for {MessageId}",
                                handlerType.Name, handler.AttemptCount, message.Id);
                        }

                        await session.SaveChangesAsync(cancellationToken);
                    }
                }
            }

            var outcome = Rollup(session, message, schedule, now);
            await session.SaveChangesAsync(cancellationToken);
            return outcome;
        }
        catch (Exception ex)
        {
            // Outside the handler loop: deserialization, DI, infrastructure. Same treatment — record
            // it and let the schedule decide, never throw at the pump.
            logger.LogError(ex, "Unexpected error processing message {MessageId} (lane {Lane})", message.Id, message.QueueName);

            if (schedule.Next(message.AttemptCount) is RetryDecision.RetryAfter retry)
            {
                message.Status = EMessageStatus.Failed;
                message.NextAttemptAtUtc = now + retry.Delay;
                await session.SaveChangesAsync(cancellationToken);
                return new Outcome(Terminal: false, message.NextAttemptAtUtc);
            }

            message.Status = EMessageStatus.DeadLettered;
            SetExpiration(session, message, now);
            await session.SaveChangesAsync(cancellationToken);
            return new Outcome(Terminal: true, null);
        }
    }

    /// <summary>Derives the message's status from its handlers.</summary>
    private Outcome Rollup(IAsyncDocumentSession session, SparkMessage message, IRetrySchedule schedule, DateTime now)
    {
        if (message.Handlers.Count == 0)
        {
            message.Status = EMessageStatus.Completed;
            message.CompletedAtUtc = now;
            SetExpiration(session, message, now);
            return new Outcome(Terminal: true, null);
        }

        var allTerminal = message.Handlers.All(h => h.Status is EHandlerStatus.Completed or EHandlerStatus.DeadLettered);
        if (allTerminal)
        {
            var allDeadLettered = message.Handlers.All(h => h.Status == EHandlerStatus.DeadLettered);
            message.Status = allDeadLettered ? EMessageStatus.DeadLettered : EMessageStatus.Completed;
            message.CompletedAtUtc = now;
            SetExpiration(session, message, now);
            return new Outcome(Terminal: true, null);
        }

        // Some handler still has work. The message's next attempt is the LATEST of its retrying
        // handlers' own next attempts: handlers share a schedule but can sit on different rungs, and
        // taking the earliest would run a handler ahead of its own ladder — shortening a backoff
        // somebody chose deliberately.
        var delays = message.Handlers
            .Where(h => h.Status is EHandlerStatus.Failed or EHandlerStatus.Pending)
            .Select(h => schedule.Next(Math.Max(h.AttemptCount, 1)))
            .OfType<RetryDecision.RetryAfter>()
            .Select(r => r.Delay)
            .ToList();

        message.Status = EMessageStatus.Failed;
        message.NextAttemptAtUtc = now + (delays.Count > 0 ? delays.Max() : TimeSpan.Zero);

        logger.LogInformation("Message {MessageId} has failing handlers, retrying at {NextAttempt}",
            message.Id, message.NextAttemptAtUtc);

        return new Outcome(Terminal: false, message.NextAttemptAtUtc);
    }

    private async Task<Outcome> DeadLetterMessageAsync(
        IAsyncDocumentSession session, SparkMessage message, DateTime now, string reason, CancellationToken cancellationToken)
    {
        logger.LogError("Dead-lettering message {MessageId}: {Reason}", message.Id, reason);
        message.Status = EMessageStatus.DeadLettered;
        SetExpiration(session, message, now);
        await session.SaveChangesAsync(cancellationToken);
        return new Outcome(Terminal: true, null);
    }

    private static async Task DeadLetterHandlerAsync(
        IAsyncDocumentSession session, SparkMessage message, HandlerExecution handler, string reason, CancellationToken cancellationToken)
    {
        handler.Status = EHandlerStatus.DeadLettered;
        handler.LastError = reason;
        await session.SaveChangesAsync(cancellationToken);
    }

    private void SetExpiration(IAsyncDocumentSession session, SparkMessage message, DateTime now)
    {
        if (options.RetentionDays <= 0) return;

        var metadata = session.Advanced.GetMetadataFor(message);
        metadata[Constants.Documents.Metadata.Expires] = now.AddDays(options.RetentionDays);
    }

    private static bool IsNonRetryable(Exception ex)
        => ex is NonRetryableException || ex.InnerException is NonRetryableException;
}
