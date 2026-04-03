using System.Reflection;
using Microsoft.Extensions.Options;
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

    public MessageSubscriptionWorker(
        string queueName,
        IDocumentStore store,
        IServiceProvider serviceProvider,
        IOptions<SparkMessagingOptions> options,
        ILogger<MessageSubscriptionWorker> logger)
        : base(store, logger)
    {
        _queueName = queueName;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        return new SubscriptionCreationOptions
        {
            Query = $@"from SparkMessages where QueueName = '{EscapeRql(_queueName)}' and Status = '{nameof(EMessageStatus.Pending)}' and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())"
        };
    }

    private static string EscapeRql(string value)
    {
        return value.Replace("'", "\\'");
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

                var recipientInterfaceType = typeof(IRecipient<>).MakeGenericType(clrType);
                var checkpointInterfaceType = typeof(ICheckpointRecipient<>).MakeGenericType(clrType);

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
                                var checkpointHandleMethod = checkpointInterfaceType.GetMethod(
                                    nameof(ICheckpointRecipient<object>.HandleAsync),
                                    [clrType, typeof(string), typeof(CancellationToken)]);
                                await (Task)checkpointHandleMethod!.Invoke(recipientInstance, [payload, handler.Checkpoint, cancellationToken])!;
                            }
                            else
                            {
                                var handleMethod = recipientInterfaceType.GetMethod(nameof(IRecipient<object>.HandleAsync));
                                await (Task)handleMethod!.Invoke(recipientInstance, [payload, cancellationToken])!;
                            }

                            handler.Status = EHandlerStatus.Completed;
                            handler.CompletedAtUtc = DateTime.UtcNow;
                            await session.SaveChangesAsync(cancellationToken);

                            Logger.LogDebug("Handler {HandlerType} completed for message {MessageId}", handlerType.Name, sparkMessage.Id);
                        }
                        catch (Exception ex) when (IsNonRetryable(ex))
                        {
                            Logger.LogWarning(ex, "Non-retryable error in handler {HandlerType} for message {MessageId}, dead-lettering handler",
                                handlerType.Name, sparkMessage.Id);

                            handler.Status = EHandlerStatus.DeadLettered;
                            handler.LastError = ex.Message;
                            await session.SaveChangesAsync(cancellationToken);
                        }
                        catch (TargetInvocationException ex) when (IsNonRetryable(ex.InnerException!))
                        {
                            Logger.LogWarning(ex.InnerException, "Non-retryable error in handler {HandlerType} for message {MessageId}, dead-lettering handler",
                                handlerType.Name, sparkMessage.Id);

                            handler.Status = EHandlerStatus.DeadLettered;
                            handler.LastError = ex.InnerException!.Message;
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
