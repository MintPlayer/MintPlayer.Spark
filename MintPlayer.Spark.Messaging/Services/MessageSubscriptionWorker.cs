using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;

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
        // Use RQL query to filter messages server-side.
        // Only Pending messages with no scheduled delay (or past their delay) are delivered.
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
                // Mark as Processing
                sparkMessage.Status = EMessageStatus.Processing;
                await session.SaveChangesAsync(cancellationToken);

                // Deserialize the payload
                var clrType = Type.GetType(sparkMessage.MessageType);
                if (clrType == null)
                {
                    Logger.LogError("Cannot resolve type {MessageType} for message {MessageId}", sparkMessage.MessageType, sparkMessage.Id);
                    sparkMessage.Status = EMessageStatus.DeadLettered;
                    sparkMessage.LastError = $"Cannot resolve type: {sparkMessage.MessageType}";
                    SetExpiration(session, sparkMessage);
                    await session.SaveChangesAsync(cancellationToken);
                    return;
                }

                var payload = JsonSerializer.Deserialize(sparkMessage.PayloadJson, clrType);
                if (payload == null)
                {
                    Logger.LogError("Failed to deserialize payload for message {MessageId}", sparkMessage.Id);
                    sparkMessage.Status = EMessageStatus.DeadLettered;
                    sparkMessage.LastError = "Failed to deserialize payload";
                    SetExpiration(session, sparkMessage);
                    await session.SaveChangesAsync(cancellationToken);
                    return;
                }

                // Resolve recipients from DI
                var recipientInterfaceType = typeof(IRecipient<>).MakeGenericType(clrType);
                using (var scope = _serviceProvider.CreateScope())
                {
                    var recipients = scope.ServiceProvider.GetServices(recipientInterfaceType).ToList();
                    if (recipients.Count == 0)
                    {
                        Logger.LogWarning("No recipients registered for message type {MessageType}, marking completed", clrType.FullName);
                    }

                    var handleMethod = recipientInterfaceType.GetMethod(nameof(IRecipient<object>.HandleAsync));

                    foreach (var recipient in recipients)
                    {
                        Logger.LogDebug("Invoking {RecipientType}.HandleAsync for message {MessageId}", recipient!.GetType().Name, sparkMessage.Id);
                        await (Task)handleMethod!.Invoke(recipient, [payload, cancellationToken])!;
                    }
                }

                // Mark completed with expiration
                sparkMessage.Status = EMessageStatus.Completed;
                sparkMessage.CompletedAtUtc = DateTime.UtcNow;
                SetExpiration(session, sparkMessage);
                await session.SaveChangesAsync(cancellationToken);

                Logger.LogInformation("Message {MessageId} (queue: {QueueName}) processed successfully", sparkMessage.Id, sparkMessage.QueueName);
            }
            catch (Exception ex) when (IsNonRetryable(ex))
            {
                Logger.LogWarning(ex, "Non-retryable error for message {MessageId} (queue: {QueueName}), dead-lettering immediately",
                    sparkMessage.Id, sparkMessage.QueueName);

                sparkMessage.Status = EMessageStatus.DeadLettered;
                sparkMessage.LastError = ex.Message;
                SetExpiration(session, sparkMessage);
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing message {MessageId} (queue: {QueueName})", sparkMessage.Id, sparkMessage.QueueName);

                sparkMessage.AttemptCount++;
                sparkMessage.LastError = ex.Message;

                if (sparkMessage.AttemptCount >= sparkMessage.MaxAttempts)
                {
                    sparkMessage.Status = EMessageStatus.DeadLettered;
                    SetExpiration(session, sparkMessage);
                    Logger.LogWarning("Message {MessageId} dead-lettered after {AttemptCount} attempts", sparkMessage.Id, sparkMessage.AttemptCount);
                }
                else
                {
                    sparkMessage.Status = EMessageStatus.Failed;
                    var delayIndex = Math.Min(sparkMessage.AttemptCount - 1, _options.BackoffDelays.Length - 1);
                    sparkMessage.NextAttemptAtUtc = DateTime.UtcNow + _options.BackoffDelays[delayIndex];
                    Logger.LogInformation("Message {MessageId} failed (attempt {AttemptCount}), retrying at {NextAttempt}",
                        sparkMessage.Id, sparkMessage.AttemptCount, sparkMessage.NextAttemptAtUtc);
                }

                await session.SaveChangesAsync(cancellationToken);
            }
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
