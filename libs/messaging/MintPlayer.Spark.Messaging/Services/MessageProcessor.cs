using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Runs one <see cref="SparkMessage"/> through its handlers. This is the whole per-message
/// contract — allow-listing, handler materialization, per-handler retry accounting, the
/// status roll-up and the park — and it is deliberately the <b>only</b> copy of it.
/// <para>
/// It was extracted from <c>MessageSubscriptionWorker</c> unchanged when messaging moved to a
/// single shared subscription. Both consumers now call it: <see cref="MessagePump"/> in
/// <see cref="ESubscriptionMode.SingleSubscription"/> mode, and
/// <c>MessageSubscriptionWorker</c> in <see cref="ESubscriptionMode.SubscriptionPerQueue"/>
/// mode. Keeping one copy is the point — the two modes must not be able to drift in how a
/// message is handled, only in how it is delivered.
/// </para>
/// </summary>
internal sealed partial class MessageProcessor
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> optionsAccessor;
    [Inject] private readonly ILogger<MessageProcessor> logger;

    private SparkMessagingOptions Options => optionsAccessor.Value;

    /// <summary>
    /// Loads the message, verifies this process still owns the claim, and runs the handlers.
    /// </summary>
    /// <param name="messageId">Document id of the message to process.</param>
    /// <param name="ownerId">
    /// The claim identity this process holds. Processing is skipped when the stored
    /// <see cref="SparkMessage.OwnerId"/> no longer matches, which happens when
    /// <see cref="MessageRetrySweeper"/> judged the claim abandoned and returned the message to
    /// the queue. Bailing out is the correct response: the message is already scheduled for
    /// redelivery, and continuing would process it twice.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelled on host shutdown. Note that the park in the outer catch deliberately does
    /// <b>not</b> use this token — see the comment there.
    /// </param>
    public async Task ProcessAsync(string messageId, string ownerId, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var sparkMessage = await session.LoadAsync<SparkMessage>(messageId, cancellationToken);

        if (sparkMessage is null)
        {
            // Expired by retention, or deleted. Nothing to do and nothing wrong.
            logger.LogDebug("Message {MessageId} no longer exists; skipping", messageId);
            return;
        }

        if (sparkMessage.OwnerId != ownerId)
        {
            logger.LogWarning(
                "Message {MessageId} is owned by {ActualOwner}, not {ExpectedOwner} — the claim was "
                + "reclaimed while queued, so it will be redelivered rather than processed here",
                messageId, sparkMessage.OwnerId ?? "<none>", ownerId);
            return;
        }

        await RunHandlersAsync(session, sparkMessage, cancellationToken);
    }

    /// <summary>
    /// Runs the handlers for a message already loaded in <paramref name="session"/>. Used
    /// directly by <c>MessageSubscriptionWorker</c>, which gets its message from the
    /// subscription batch rather than by id.
    /// </summary>
    public async Task RunHandlersAsync(
        IAsyncDocumentSession session,
        SparkMessage sparkMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            // WakeUp is consumed here: it must be false on every subsequent save, or a message
            // parked for another backoff round would still match the subscription query and be
            // redelivered immediately in a tight loop.
            sparkMessage.WakeUp = false;

            // R2-H6: allow-list check BEFORE Type.GetType. The DI-derived allow-list contains
            // only types that have an IRecipient<T> registration; an attacker who can write into
            // SparkMessages can no longer route through Type.GetType to instantiate arbitrary
            // types (Newtonsoft gadgets, etc.).
            var allowList = serviceProvider.GetRequiredService<IMessageTypeAllowList>();
            if (!allowList.IsAllowedMessageType(sparkMessage.MessageType))
            {
                logger.LogError(
                    "Message type {MessageType} is not in the allow-list (no registered IRecipient<>) — dead-lettering {MessageId}",
                    sparkMessage.MessageType, sparkMessage.Id);
                DeadLetter(session, sparkMessage);
                await session.SaveChangesAsync(cancellationToken);
                return;
            }

            var clrType = Type.GetType(sparkMessage.MessageType);
            if (clrType == null)
            {
                logger.LogError("Cannot resolve type {MessageType} for message {MessageId}", sparkMessage.MessageType, sparkMessage.Id);
                DeadLetter(session, sparkMessage);
                await session.SaveChangesAsync(cancellationToken);
                return;
            }

            var payload = JsonConvert.DeserializeObject(sparkMessage.PayloadJson, clrType);
            if (payload == null)
            {
                logger.LogError("Failed to deserialize payload for message {MessageId}", sparkMessage.Id);
                DeadLetter(session, sparkMessage);
                await session.SaveChangesAsync(cancellationToken);
                return;
            }

            // Cache the closed generic interface types per CLR message type — the MakeGenericType
            // call is otherwise repeated for every message processed.
            var recipientInterfaceType = ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
                ("MessageProcessor.RecipientInterface", clrType),
                static k => typeof(IRecipient<>).MakeGenericType(k.Type));
            var checkpointInterfaceType = ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
                ("MessageProcessor.CheckpointRecipientInterface", clrType),
                static k => typeof(ICheckpointRecipient<>).MakeGenericType(k.Type));

            using (var scope = serviceProvider.CreateScope())
            {
                // Populate handler list on first pickup
                if (sparkMessage.Handlers.Count == 0)
                {
                    var recipients = scope.ServiceProvider.GetServices(recipientInterfaceType).ToList();
                    foreach (var recipient in recipients)
                    {
                        sparkMessage.Handlers.Add(new HandlerExecution
                        {
                            HandlerType = recipient!.GetType().AssemblyQualifiedName!,
                            Status = EHandlerStatus.Pending,
                        });
                    }

                    if (sparkMessage.Handlers.Count == 0)
                    {
                        logger.LogWarning("No recipients registered for message type {MessageType}, marking completed", clrType.FullName);
                    }

                    await session.SaveChangesAsync(cancellationToken);
                }

                var checkpoint = scope.ServiceProvider.GetService<IMessageCheckpoint>() as MessageCheckpoint;

                // Execute each handler independently
                foreach (var handler in sparkMessage.Handlers)
                {
                    if (handler.Status is EHandlerStatus.Completed or EHandlerStatus.DeadLettered)
                        continue;

                    // R2-H6: allow-list the handler too. HandlerType was captured from the
                    // registered recipient at enqueue time, but the document on disk is mutable —
                    // refuse to call Type.GetType on anything we didn't register at startup.
                    if (!allowList.IsAllowedHandlerType(handler.HandlerType))
                    {
                        logger.LogError(
                            "Handler type {HandlerType} is not in the allow-list — dead-lettering handler on {MessageId}",
                            handler.HandlerType, sparkMessage.Id);
                        handler.Status = EHandlerStatus.DeadLettered;
                        handler.LastError = $"Handler type not in allow-list: {handler.HandlerType}";
                        await session.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    var handlerType = Type.GetType(handler.HandlerType);
                    if (handlerType == null)
                    {
                        logger.LogError("Cannot resolve handler type {HandlerType} for message {MessageId}", handler.HandlerType, sparkMessage.Id);
                        handler.Status = EHandlerStatus.DeadLettered;
                        handler.LastError = $"Cannot resolve handler type: {handler.HandlerType}";
                        await session.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    var recipientInstance = scope.ServiceProvider.GetServices(recipientInterfaceType)
                        .FirstOrDefault(r => r!.GetType() == handlerType);

                    if (recipientInstance == null)
                    {
                        logger.LogError("Handler {HandlerType} not found in DI for message {MessageId}", handlerType.Name, sparkMessage.Id);
                        handler.Status = EHandlerStatus.DeadLettered;
                        handler.LastError = $"Handler not found in DI: {handlerType.Name}";
                        await session.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    checkpoint?.SetContext(session, handler);

                    try
                    {
                        logger.LogDebug("Invoking {HandlerType}.HandleAsync for message {MessageId}", handlerType.Name, sparkMessage.Id);

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
                        handler.CompletedAtUtc = DateTime.UtcNow;
                        await session.SaveChangesAsync(cancellationToken);

                        logger.LogDebug("Handler {HandlerType} completed for message {MessageId}", handlerType.Name, sparkMessage.Id);
                    }
                    catch (TargetInvocationException ex) when (IsNonRetryable(ex.InnerException!))
                    {
                        logger.LogWarning(ex.InnerException, "Non-retryable error in handler {HandlerType} for message {MessageId}, dead-lettering handler",
                            handlerType.Name, sparkMessage.Id);

                        handler.Status = EHandlerStatus.DeadLettered;
                        handler.LastError = ex.InnerException!.Message;
                        await session.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception ex) when (IsNonRetryable(ex))
                    {
                        logger.LogWarning(ex, "Non-retryable error in handler {HandlerType} for message {MessageId}, dead-lettering handler",
                            handlerType.Name, sparkMessage.Id);

                        handler.Status = EHandlerStatus.DeadLettered;
                        handler.LastError = ex.Message;
                        await session.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var actualException = ex is TargetInvocationException tie ? tie.InnerException! : ex;

                        logger.LogError(actualException, "Error in handler {HandlerType} for message {MessageId}", handlerType.Name, sparkMessage.Id);

                        handler.AttemptCount++;
                        handler.LastError = actualException.Message;

                        if (handler.AttemptCount >= sparkMessage.MaxAttempts)
                        {
                            handler.Status = EHandlerStatus.DeadLettered;
                            logger.LogWarning("Handler {HandlerType} dead-lettered after {AttemptCount} attempts for message {MessageId}",
                                handlerType.Name, handler.AttemptCount, sparkMessage.Id);
                        }
                        else
                        {
                            handler.Status = EHandlerStatus.Failed;
                        }

                        await session.SaveChangesAsync(cancellationToken);
                    }
                }
            }

            RollupMessageStatus(sparkMessage, session);
            await session.SaveChangesAsync(cancellationToken);

            if (sparkMessage.Status == EMessageStatus.Completed)
            {
                logger.LogInformation("Message {MessageId} (queue: {QueueName}) processed successfully", sparkMessage.Id, sparkMessage.QueueName);
            }
        }
        catch (Exception ex)
        {
            // Unexpected error outside the handler loop (deserialization, DI, etc.)
            logger.LogError(ex, "Unexpected error processing message {MessageId} (queue: {QueueName})", sparkMessage.Id, sparkMessage.QueueName);

            sparkMessage.Status = EMessageStatus.Failed;
            sparkMessage.OwnerId = null;
            sparkMessage.ClaimExpiresAtUtc = null;
            var delayIndex = Math.Min(sparkMessage.AttemptCount - 1, Options.ResolvedBackoffDelays.Length - 1);
            sparkMessage.NextAttemptAtUtc = DateTime.UtcNow + Options.ResolvedBackoffDelays[Math.Max(0, delayIndex)];

            // CancellationToken.None, deliberately. This save is the park that makes the message
            // retryable, and the usual reason we are here on shutdown is that `cancellationToken`
            // has just fired — passing it would cancel the very write that records "try again
            // later", leaving the message at Processing with nothing scheduled. That was half of
            // why a message could strand for ever. The sweeper's reclaim now covers the other
            // half, but a park that throws on the way out is still a park that did not happen.
            try
            {
                await session.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx,
                    "Failed to park message {MessageId} after an error; it stays at Processing and will be "
                    + "reclaimed once its claim expires", sparkMessage.Id);
            }
        }
    }

    private void DeadLetter(IAsyncDocumentSession session, SparkMessage sparkMessage)
    {
        sparkMessage.Status = EMessageStatus.DeadLettered;
        sparkMessage.OwnerId = null;
        sparkMessage.ClaimExpiresAtUtc = null;
        SetExpiration(session, sparkMessage);
    }

    private void RollupMessageStatus(SparkMessage sparkMessage, IAsyncDocumentSession session)
    {
        if (sparkMessage.Handlers.Count == 0)
        {
            sparkMessage.Status = EMessageStatus.Completed;
            sparkMessage.CompletedAtUtc = DateTime.UtcNow;
            sparkMessage.OwnerId = null;
            sparkMessage.ClaimExpiresAtUtc = null;
            SetExpiration(session, sparkMessage);
            return;
        }

        var hasAnyFailed = sparkMessage.Handlers.Any(h => h.Status == EHandlerStatus.Failed);
        var hasAnyPending = sparkMessage.Handlers.Any(h => h.Status == EHandlerStatus.Pending);
        var allTerminal = sparkMessage.Handlers.All(h => h.Status is EHandlerStatus.Completed or EHandlerStatus.DeadLettered);

        if (allTerminal)
        {
            var allDeadLettered = sparkMessage.Handlers.All(h => h.Status == EHandlerStatus.DeadLettered);
            sparkMessage.Status = allDeadLettered ? EMessageStatus.DeadLettered : EMessageStatus.Completed;
            sparkMessage.CompletedAtUtc = DateTime.UtcNow;
            sparkMessage.OwnerId = null;
            sparkMessage.ClaimExpiresAtUtc = null;
            SetExpiration(session, sparkMessage);
        }
        else if (hasAnyFailed || hasAnyPending)
        {
            // Schedule retry based on the highest attempt count among failed handlers. The claim
            // is released: the message is parked, so nothing is working on it, and leaving an
            // owner behind would make the sweeper's reclaim query miss it.
            sparkMessage.Status = EMessageStatus.Failed;
            sparkMessage.OwnerId = null;
            sparkMessage.ClaimExpiresAtUtc = null;
            var maxAttempt = sparkMessage.Handlers
                .Where(h => h.Status == EHandlerStatus.Failed)
                .Select(h => h.AttemptCount)
                .DefaultIfEmpty(0)
                .Max();
            var delayIndex = Math.Min(maxAttempt - 1, Options.ResolvedBackoffDelays.Length - 1);
            sparkMessage.NextAttemptAtUtc = DateTime.UtcNow + Options.ResolvedBackoffDelays[Math.Max(0, delayIndex)];

            logger.LogInformation("Message {MessageId} has failing handlers, retrying at {NextAttempt}",
                sparkMessage.Id, sparkMessage.NextAttemptAtUtc);
        }
    }

    private void SetExpiration(IAsyncDocumentSession session, SparkMessage msg)
    {
        if (Options.RetentionDays <= 0) return;

        var metadata = session.Advanced.GetMetadataFor(msg);
        metadata[Constants.Documents.Metadata.Expires] = DateTime.UtcNow.AddDays(Options.RetentionDays);
    }

    private static bool IsNonRetryable(Exception ex)
    {
        return ex is NonRetryableException
            || ex.InnerException is NonRetryableException;
    }
}
