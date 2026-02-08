using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Messaging.Services;

internal class MessageProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDocumentStore _documentStore;
    private readonly RecipientRegistry _recipientRegistry;
    private readonly SparkMessagingOptions _options;
    private readonly ILogger<MessageProcessor> _logger;
    private readonly SemaphoreSlim _signal = new(0);

    public MessageProcessor(
        IServiceProvider serviceProvider,
        IDocumentStore documentStore,
        RecipientRegistry recipientRegistry,
        IOptions<SparkMessagingOptions> options,
        ILogger<MessageProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _documentStore = documentStore;
        _recipientRegistry = recipientRegistry;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changes = _documentStore.Changes();
        await changes.EnsureConnectedNow();

        var observable = changes.ForDocumentsInCollection<SparkMessage>();
        using var subscription = observable.Subscribe(new DocumentChangeObserver(_signal));

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

        _logger.LogInformation("MessageProcessor stopping");
    }

    private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
    {
        using var session = _documentStore.OpenAsyncSession();

        var now = DateTime.UtcNow;

        var actionableMessages = await session
            .Query<SparkMessage, Indexes.SparkMessages_ByQueue>()
            .Where(m =>
                (m.Status == EMessageStatus.Pending && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)) ||
                (m.Status == EMessageStatus.Failed && m.NextAttemptAtUtc != null && m.NextAttemptAtUtc <= now))
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(stoppingToken);

        if (actionableMessages.Count == 0)
            return;

        _logger.LogDebug("Found {Count} actionable messages", actionableMessages.Count);

        // Group by queue, take the oldest per queue
        var queues = actionableMessages
            .GroupBy(m => m.QueueName)
            .Select(g => g.First())
            .ToList();

        await Task.WhenAll(queues.Select(msg => ProcessSingleMessageAsync(msg, stoppingToken)));
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

            var recipientTypes = _recipientRegistry.GetRecipientTypes(clrType);
            if (recipientTypes.Count == 0)
            {
                _logger.LogWarning("No recipients registered for message type {MessageType}, marking completed", clrType.FullName);
            }

            // Create DI scope and invoke all recipients
            using (var scope = _serviceProvider.CreateScope())
            {
                foreach (var recipientType in recipientTypes)
                {
                    var recipient = ActivatorUtilities.CreateInstance(scope.ServiceProvider, recipientType);
                    var handleMethod = recipientType.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance);

                    if (handleMethod == null)
                    {
                        _logger.LogError("HandleAsync method not found on {RecipientType}", recipientType.FullName);
                        continue;
                    }

                    _logger.LogDebug("Invoking {RecipientType}.HandleAsync for message {MessageId}", recipientType.Name, sparkMessage.Id);
                    await (Task)handleMethod.Invoke(recipient, new[] { payload, stoppingToken })!;
                }
            }

            // Mark completed
            using (var session = _documentStore.OpenAsyncSession())
            {
                var msg = await session.LoadAsync<SparkMessage>(sparkMessage.Id, stoppingToken);
                if (msg == null) return;

                msg.Status = EMessageStatus.Completed;
                msg.CompletedAtUtc = DateTime.UtcNow;
                await session.SaveChangesAsync(stoppingToken);
            }

            _logger.LogInformation("Message {MessageId} (queue: {QueueName}) processed successfully", sparkMessage.Id, sparkMessage.QueueName);
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
            await session.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dead-letter message {MessageId}", messageId);
        }
    }

    private sealed class DocumentChangeObserver : IObserver<DocumentChange>
    {
        private readonly SemaphoreSlim _signal;

        public DocumentChangeObserver(SemaphoreSlim signal) => _signal = signal;

        public void OnNext(DocumentChange value) => _signal.Release();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
