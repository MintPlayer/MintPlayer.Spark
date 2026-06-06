using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Messaging.Services;

internal sealed class MessageSubscriptionWorker : SparkSubscriptionWorker<SparkMessage>
{
    private readonly string _queueName;
    private readonly IServiceProvider _serviceProvider;
    private readonly SparkMessagingOptions _options;

    protected override string SubscriptionName => $"SparkMessaging-{_queueName}";
    protected override int MaxDocsPerBatch => 1;

    // Hand-written ctor — the queueName is a per-instance runtime value supplied by
    // MessageSubscriptionManager and can't be DI-resolved, so [Inject] doesn't apply here.
    // Forwards (store, loggerFactory) to the base (which uses [PostConstruct] to derive the
    // per-class logger via loggerFactory.CreateLogger(GetType())).
    public MessageSubscriptionWorker(
        string queueName,
        IDocumentStore store,
        IServiceProvider serviceProvider,
        IOptions<SparkMessagingOptions> options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory, store)
    {
        _queueName = queueName;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        // R2-H14: refuse queue names that don't match the strict identifier
        // allowlist. The previous EscapeRql helper only escaped single-quote and
        // missed backslash escapes — `\` itself wasn't escaped, so a name like
        // `\\'` round-tripped to `\\\'` and still closed the literal. The set of
        // valid queue names is bounded (developer-declared via attribute, or
        // explicit override) so a strict allowlist is the right shape: letters,
        // digits, dot, underscore, dash. Throw at startup so the operator sees
        // the bad config before traffic flows.
        if (!IsValidQueueName(_queueName))
            throw new InvalidOperationException(
                $"Invalid Spark message queue name '{_queueName}'. Queue names must match [A-Za-z0-9._-]+.");

        return new SubscriptionCreationOptions
        {
            Query = $@"from SparkMessages where QueueName = '{_queueName}' and Status = '{nameof(EMessageStatus.Pending)}' and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())"
        };
    }

    private static bool IsValidQueueName(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            // Allow standard identifier shape plus '+' (nested CLR type separator —
            // typeof(Nested).FullName produces "Outer+Inner") and '`' / digits for
            // generic type parameters. Critically disallowed: `'`, `\`, whitespace,
            // anything that could break out of the single-quoted RQL literal.
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '+' || c == '`'))
                return false;
        }
        return true;
    }

    protected override async Task ProcessBatchAsync(SubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        foreach (var item in batch.Items)
        {
            var sparkMessage = item.Result;
            var session = batch.OpenAsyncSession();

            try
            {
                // Mark as Processing and increment pickup count
                sparkMessage.Status = EMessageStatus.Processing;
                sparkMessage.AttemptCount++;

                // Deserialize the payload
                // R2-H6: allow-list check BEFORE Type.GetType. The DI-derived
                // allow-list contains only types that have an IRecipient<T>
                // registration; an attacker who can write into SparkMessages
                // (pre-mTLS, via the previously-unauthenticated sync/apply path)
                // can no longer route through Type.GetType to instantiate
                // arbitrary types (Newtonsoft gadgets, etc.).
                var allowList = _serviceProvider.GetRequiredService<IMessageTypeAllowList>();
                if (!allowList.IsAllowedMessageType(sparkMessage.MessageType))
                {
                    Logger.LogError(
                        "Message type {MessageType} is not in the allow-list (no registered IRecipient<>) — dead-lettering {MessageId}",
                        sparkMessage.MessageType, sparkMessage.Id);
                    sparkMessage.Status = EMessageStatus.DeadLettered;
                    SetExpiration(session, sparkMessage);
                    await session.SaveChangesAsync(cancellationToken);
                    return;
                }

                var clrType = Type.GetType(sparkMessage.MessageType);
                if (clrType == null)
                {
                    Logger.LogError("Cannot resolve type {MessageType} for message {MessageId}", sparkMessage.MessageType, sparkMessage.Id);
                    sparkMessage.Status = EMessageStatus.DeadLettered;
                    SetExpiration(session, sparkMessage);
                    await session.SaveChangesAsync(cancellationToken);
                    return;
                }

                var payload = JsonConvert.DeserializeObject(sparkMessage.PayloadJson, clrType);
                if (payload == null)
                {
                    Logger.LogError("Failed to deserialize payload for message {MessageId}", sparkMessage.Id);
                    sparkMessage.Status = EMessageStatus.DeadLettered;
                    SetExpiration(session, sparkMessage);
                    await session.SaveChangesAsync(cancellationToken);
                    return;
                }

                // Cache the closed generic interface types per CLR message type — the
                // MakeGenericType call is otherwise repeated for every message processed.
                var recipientInterfaceType = ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
                    ("MessageSubscriptionWorker.RecipientInterface", clrType),
                    static k => typeof(IRecipient<>).MakeGenericType(k.Type));
                var checkpointInterfaceType = ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
                    ("MessageSubscriptionWorker.CheckpointRecipientInterface", clrType),
                    static k => typeof(ICheckpointRecipient<>).MakeGenericType(k.Type));

                using (var scope = _serviceProvider.CreateScope())
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
                            Logger.LogWarning("No recipients registered for message type {MessageType}, marking completed", clrType.FullName);
                        }

                        await session.SaveChangesAsync(cancellationToken);
                    }

                    // Resolve the IMessageCheckpoint service from the scope
                    var checkpoint = scope.ServiceProvider.GetService<IMessageCheckpoint>() as MessageCheckpoint;

                    // Execute each handler independently
                    foreach (var handler in sparkMessage.Handlers)
                    {
                        // Skip already completed or dead-lettered handlers
                        if (handler.Status is EHandlerStatus.Completed or EHandlerStatus.DeadLettered)
                            continue;

                        // R2-H6: allow-list the handler too. HandlerType was
                        // captured from the registered recipient at enqueue time,
                        // but the document on disk is mutable — refuse to call
                        // Type.GetType on anything we didn't register at startup.
                        if (!allowList.IsAllowedHandlerType(handler.HandlerType))
                        {
                            Logger.LogError(
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
                            Logger.LogError("Cannot resolve handler type {HandlerType} for message {MessageId}", handler.HandlerType, sparkMessage.Id);
                            handler.Status = EHandlerStatus.DeadLettered;
                            handler.LastError = $"Cannot resolve handler type: {handler.HandlerType}";
                            await session.SaveChangesAsync(cancellationToken);
                            continue;
                        }

                        var recipientInstance = scope.ServiceProvider.GetServices(recipientInterfaceType)
                            .FirstOrDefault(r => r!.GetType() == handlerType);

                        if (recipientInstance == null)
                        {
                            Logger.LogError("Handler {HandlerType} not found in DI for message {MessageId}", handlerType.Name, sparkMessage.Id);
                            handler.Status = EHandlerStatus.DeadLettered;
                            handler.LastError = $"Handler not found in DI: {handlerType.Name}";
                            await session.SaveChangesAsync(cancellationToken);
                            continue;
                        }

                        // Set up checkpoint context for this handler
                        checkpoint?.SetContext(session, handler);

                        try
                        {
                            Logger.LogDebug("Invoking {HandlerType}.HandleAsync for message {MessageId}", handlerType.Name, sparkMessage.Id);

                            // Check if handler implements ICheckpointRecipient<T> and has a previous checkpoint
                            if (handler.Checkpoint != null && checkpointInterfaceType.IsAssignableFrom(handlerType))
                            {
                                var checkpointInterface = checkpointInterfaceType;
                                var msgType = clrType;
                                var checkpointHandleMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
                                    ("MessageSubscriptionWorker.CheckpointHandleAsync", clrType),
                                    _ => checkpointInterface.GetMethod(
                                        nameof(ICheckpointRecipient<object>.HandleAsync),
                                        [msgType, typeof(string), typeof(CancellationToken)]));
                                await (Task)checkpointHandleMethod!.Invoke(recipientInstance, [payload, handler.Checkpoint, cancellationToken])!;
                            }
                            else
                            {
                                var recipientInterface = recipientInterfaceType;
                                var handleMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
                                    ("MessageSubscriptionWorker.RecipientHandleAsync", clrType),
                                    _ => recipientInterface.GetMethod(nameof(IRecipient<object>.HandleAsync)));
                                await (Task)handleMethod!.Invoke(recipientInstance, [payload, cancellationToken])!;
                            }

                            handler.Status = EHandlerStatus.Completed;
                            handler.CompletedAtUtc = DateTime.UtcNow;
                            await session.SaveChangesAsync(cancellationToken);

                            Logger.LogDebug("Handler {HandlerType} completed for message {MessageId}", handlerType.Name, sparkMessage.Id);
                        }
                        catch (TargetInvocationException ex) when (IsNonRetryable(ex.InnerException!))
                        {
                            Logger.LogWarning(ex.InnerException, "Non-retryable error in handler {HandlerType} for message {MessageId}, dead-lettering handler",
                                handlerType.Name, sparkMessage.Id);

                            handler.Status = EHandlerStatus.DeadLettered;
                            handler.LastError = ex.InnerException!.Message;
                            await session.SaveChangesAsync(cancellationToken);
                        }
                        catch (Exception ex) when (IsNonRetryable(ex))
                        {
                            Logger.LogWarning(ex, "Non-retryable error in handler {HandlerType} for message {MessageId}, dead-lettering handler",
                                handlerType.Name, sparkMessage.Id);

                            handler.Status = EHandlerStatus.DeadLettered;
                            handler.LastError = ex.Message;
                            await session.SaveChangesAsync(cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            var actualException = ex is TargetInvocationException tie ? tie.InnerException! : ex;

                            Logger.LogError(actualException, "Error in handler {HandlerType} for message {MessageId}", handlerType.Name, sparkMessage.Id);

                            handler.AttemptCount++;
                            handler.LastError = actualException.Message;

                            if (handler.AttemptCount >= sparkMessage.MaxAttempts)
                            {
                                handler.Status = EHandlerStatus.DeadLettered;
                                Logger.LogWarning("Handler {HandlerType} dead-lettered after {AttemptCount} attempts for message {MessageId}",
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

                // Determine message-level status from handler rollup
                RollupMessageStatus(sparkMessage, session);
                await session.SaveChangesAsync(cancellationToken);

                if (sparkMessage.Status == EMessageStatus.Completed)
                {
                    Logger.LogInformation("Message {MessageId} (queue: {QueueName}) processed successfully", sparkMessage.Id, sparkMessage.QueueName);
                }
            }
            catch (Exception ex)
            {
                // Unexpected error outside handler loop (deserialization, DI, etc.)
                Logger.LogError(ex, "Unexpected error processing message {MessageId} (queue: {QueueName})", sparkMessage.Id, sparkMessage.QueueName);

                sparkMessage.Status = EMessageStatus.Failed;
                var delayIndex = Math.Min(sparkMessage.AttemptCount - 1, _options.BackoffDelays.Length - 1);
                sparkMessage.NextAttemptAtUtc = DateTime.UtcNow + _options.BackoffDelays[Math.Max(0, delayIndex)];

                await session.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private void RollupMessageStatus(SparkMessage sparkMessage, Raven.Client.Documents.Session.IAsyncDocumentSession session)
    {
        if (sparkMessage.Handlers.Count == 0)
        {
            // No handlers — mark completed
            sparkMessage.Status = EMessageStatus.Completed;
            sparkMessage.CompletedAtUtc = DateTime.UtcNow;
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
            SetExpiration(session, sparkMessage);
        }
        else if (hasAnyFailed || hasAnyPending)
        {
            // Schedule retry based on the highest attempt count among failed handlers
            sparkMessage.Status = EMessageStatus.Failed;
            var maxAttempt = sparkMessage.Handlers
                .Where(h => h.Status == EHandlerStatus.Failed)
                .Select(h => h.AttemptCount)
                .DefaultIfEmpty(0)
                .Max();
            var delayIndex = Math.Min(maxAttempt - 1, _options.BackoffDelays.Length - 1);
            sparkMessage.NextAttemptAtUtc = DateTime.UtcNow + _options.BackoffDelays[Math.Max(0, delayIndex)];

            Logger.LogInformation("Message {MessageId} has failing handlers, retrying at {NextAttempt}",
                sparkMessage.Id, sparkMessage.NextAttemptAtUtc);
        }
    }

    private void SetExpiration(Raven.Client.Documents.Session.IAsyncDocumentSession session, SparkMessage msg)
    {
        if (_options.RetentionDays <= 0) return;

        var metadata = session.Advanced.GetMetadataFor(msg);
        metadata[Constants.Documents.Metadata.Expires] = DateTime.UtcNow.AddDays(_options.RetentionDays);
    }

    private static bool IsNonRetryable(Exception ex)
    {
        return ex is NonRetryableException
            || ex.InnerException is NonRetryableException;
    }
}
