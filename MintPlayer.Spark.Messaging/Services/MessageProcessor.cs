using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Messaging.Services;

internal class MessageProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDocumentStore _documentStore;
    private readonly SparkMessagingOptions _options;
    private readonly ILogger<MessageProcessor> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _needsReconnect;

    public MessageProcessor(
        IServiceProvider serviceProvider,
        IDocumentStore documentStore,
        IOptions<SparkMessagingOptions> options,
        ILogger<MessageProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _documentStore = documentStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IDisposable? subscription = null;

        try
        {
            subscription = await SubscribeToChangesAsync();
            _logger.LogInformation("MessageProcessor started, listening for SparkMessage changes");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_options.FallbackPollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // Drain any extra signals so we don't loop redundantly
                while (_signal.CurrentCount > 0)
                {
                    _signal.Wait(0);
                }

                // Reconnect to Changes API if the connection was lost
                if (_needsReconnect)
                {
                    _needsReconnect = false;
                    subscription?.Dispose();
                    try
                    {
                        subscription = await SubscribeToChangesAsync();
                        _logger.LogInformation("Reconnected to RavenDB Changes API");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to reconnect to Changes API, will retry next cycle");
                    }
                }

                try
                {
                    await ProcessMessagesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during message processing cycle");
                }
            }
        }
        finally
        {
            subscription?.Dispose();
        }

        _logger.LogInformation("MessageProcessor stopping");
    }

    private async Task<IDisposable> SubscribeToChangesAsync()
    {
        var changes = _documentStore.Changes();
        await changes.EnsureConnectedNow();
        var observable = changes.ForDocumentsInCollection<SparkMessage>();
        return observable.Subscribe(new DocumentChangeObserver(_signal, _logger, () => _needsReconnect = true));
    }

    /// <summary>
    /// Drain loop: keep processing until all queues are empty, then return to the wait loop.
    /// </summary>
    private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var session = _documentStore.OpenAsyncSession();

            var now = DateTime.UtcNow;

            var actionableMessages = await session
                .Query<SparkMessage, Indexes.SparkMessages_ByQueue>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(m =>
                    (m.Status == EMessageStatus.Pending && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)) ||
                    (m.Status == EMessageStatus.Failed && m.NextAttemptAtUtc != null && m.NextAttemptAtUtc <= now))
                .OrderBy(m => m.CreatedAtUtc)
                .Take(128)
                .ToListAsync(stoppingToken);

            if (actionableMessages.Count == 0)
                break;

            _logger.LogDebug("Found {Count} actionable messages", actionableMessages.Count);

            // Group by queue, take the oldest per queue
            var queues = actionableMessages
                .GroupBy(m => m.QueueName)
                .Select(g => g.First())
                .ToList();

            await Task.WhenAll(queues.Select(msg => ProcessSingleMessageAsync(msg, stoppingToken)));
        }
    }

    private async Task ProcessSingleMessageAsync(SparkMessage sparkMessage, CancellationToken stoppingToken)
    {
        try
        {
            // Mark as Processing
            using (var session = _documentStore.OpenAsyncSession())
            {
                var msg = await session.LoadAsync<SparkMessage>(sparkMessage.Id, stoppingToken);
                if (msg == null) return;

                msg.Status = EMessageStatus.Processing;
                await session.SaveChangesAsync(stoppingToken);
            }

            // Deserialize
            var clrType = Type.GetType(sparkMessage.MessageType);
            if (clrType == null)
            {
                _logger.LogError("Cannot resolve type {MessageType} for message {MessageId}", sparkMessage.MessageType, sparkMessage.Id);
                await MarkDeadLetteredAsync(sparkMessage.Id!, $"Cannot resolve type: {sparkMessage.MessageType}", stoppingToken);
                return;
            }

            var payload = JsonSerializer.Deserialize(sparkMessage.PayloadJson, clrType);
            if (payload == null)
            {
                _logger.LogError("Failed to deserialize payload for message {MessageId}", sparkMessage.Id);
                await MarkDeadLetteredAsync(sparkMessage.Id!, "Failed to deserialize payload", stoppingToken);
                return;
            }

            // Resolve recipients from DI as IRecipient<TMessage>
            var recipientInterfaceType = typeof(IRecipient<>).MakeGenericType(clrType);
            using (var scope = _serviceProvider.CreateScope())
            {
                var recipients = scope.ServiceProvider.GetServices(recipientInterfaceType).ToList();
                if (recipients.Count == 0)
                {
                    _logger.LogWarning("No recipients registered for message type {MessageType}, marking completed", clrType.FullName);
                }

                var handleMethod = recipientInterfaceType.GetMethod(nameof(IRecipient<object>.HandleAsync));

                foreach (var recipient in recipients)
                {
                    _logger.LogDebug("Invoking {RecipientType}.HandleAsync for message {MessageId}", recipient!.GetType().Name, sparkMessage.Id);
                    await (Task)handleMethod!.Invoke(recipient, [payload, stoppingToken])!;
                }
            }

            // Mark completed with expiration
            using (var session = _documentStore.OpenAsyncSession())
            {
                var msg = await session.LoadAsync<SparkMessage>(sparkMessage.Id, stoppingToken);
                if (msg == null) return;

                msg.Status = EMessageStatus.Completed;
                msg.CompletedAtUtc = DateTime.UtcNow;
                SetExpiration(session, msg);
                await session.SaveChangesAsync(stoppingToken);
            }

            _logger.LogInformation("Message {MessageId} (queue: {QueueName}) processed successfully", sparkMessage.Id, sparkMessage.QueueName);
        }
        catch (Exception ex) when (IsNonRetryable(ex))
        {
            _logger.LogWarning(ex, "Non-retryable error for message {MessageId} (queue: {QueueName}), dead-lettering immediately",
                sparkMessage.Id, sparkMessage.QueueName);
            await MarkDeadLetteredAsync(sparkMessage.Id!, ex.Message, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message {MessageId} (queue: {QueueName})", sparkMessage.Id, sparkMessage.QueueName);
            await MarkFailedAsync(sparkMessage.Id!, ex, stoppingToken);
        }
    }

    private async Task MarkFailedAsync(string messageId, Exception exception, CancellationToken stoppingToken)
    {
        try
        {
            using var session = _documentStore.OpenAsyncSession();
            var msg = await session.LoadAsync<SparkMessage>(messageId, stoppingToken);
            if (msg == null) return;

            msg.AttemptCount++;
            msg.LastError = exception.Message;

            if (msg.AttemptCount >= msg.MaxAttempts)
            {
                msg.Status = EMessageStatus.DeadLettered;
                SetExpiration(session, msg);
                _logger.LogWarning("Message {MessageId} dead-lettered after {AttemptCount} attempts", messageId, msg.AttemptCount);
            }
            else
            {
                msg.Status = EMessageStatus.Failed;
                var delayIndex = Math.Min(msg.AttemptCount - 1, _options.BackoffDelays.Length - 1);
                msg.NextAttemptAtUtc = DateTime.UtcNow + _options.BackoffDelays[delayIndex];
                _logger.LogInformation("Message {MessageId} failed (attempt {AttemptCount}), retrying at {NextAttempt}",
                    messageId, msg.AttemptCount, msg.NextAttemptAtUtc);
            }

            await session.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update message {MessageId} status after error", messageId);
        }
    }

    private async Task MarkDeadLetteredAsync(string messageId, string error, CancellationToken stoppingToken)
    {
        try
        {
            using var session = _documentStore.OpenAsyncSession();
            var msg = await session.LoadAsync<SparkMessage>(messageId, stoppingToken);
            if (msg == null) return;

            msg.Status = EMessageStatus.DeadLettered;
            msg.LastError = error;
            SetExpiration(session, msg);
            await session.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dead-letter message {MessageId}", messageId);
        }
    }

    private static bool IsNonRetryable(Exception ex)
    {
        // Check the exception itself and any inner exceptions
        return ex is NonRetryableException
            || ex.InnerException is NonRetryableException;
    }

    private void SetExpiration(Raven.Client.Documents.Session.IAsyncDocumentSession session, SparkMessage msg)
    {
        if (_options.RetentionDays <= 0) return;

        var metadata = session.Advanced.GetMetadataFor(msg);
        metadata[Constants.Documents.Metadata.Expires] = DateTime.UtcNow.AddDays(_options.RetentionDays);
    }

    private sealed class DocumentChangeObserver : IObserver<DocumentChange>
    {
        private readonly SemaphoreSlim _signal;
        private readonly ILogger _logger;
        private readonly Action _onError;

        public DocumentChangeObserver(SemaphoreSlim signal, ILogger logger, Action onError)
        {
            _signal = signal;
            _logger = logger;
            _onError = onError;
        }

        public void OnNext(DocumentChange value) => _signal.Release();

        public void OnError(Exception error)
        {
            _logger.LogWarning(error, "RavenDB Changes API connection lost, will reconnect");
            _onError();
            _signal.Release();
        }

        public void OnCompleted() { }
    }
}
