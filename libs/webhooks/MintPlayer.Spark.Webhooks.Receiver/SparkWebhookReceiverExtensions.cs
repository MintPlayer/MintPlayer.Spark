using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Webhooks.Receiver;

public static class SparkWebhookReceiverExtensions
{
    /// <summary>
    /// POST <paramref name="path"/> receives Standard Webhooks from one producer:
    /// <list type="number">
    /// <item>verify the signature (<paramref name="configure"/> sets the secrets; 401 otherwise);</item>
    /// <item>enqueue the event once per <c>webhook-id</c> on Spark Messaging, so a retried or redelivered event is not handled twice;</item>
    /// <item>answer 204. Your <c>IRecipient&lt;WebhookMessage&lt;T&gt;&gt;</c> runs afterwards, with Messaging's retries.</item>
    /// </list>
    /// <paramref name="eventTypes"/> maps each event type to its data class: pass the generated processor's <c>EventTypes</c>
    /// (<c>spark webhooks generate --lang cs</c>). An event type outside it, or one nobody receives, goes out as
    /// <see cref="UnknownWebhookMessage"/> when something receives that, and is otherwise acknowledged and dropped.
    /// Needs <c>spark.AddMessaging()</c>.
    /// </summary>
    public static ISparkBuilder AddWebhookReceiver(this ISparkBuilder builder, string path, IReadOnlyDictionary<string, Type> eventTypes, Action<WebhookReceiverOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(eventTypes);
        ArgumentNullException.ThrowIfNull(configure);

        var name = "Spark.Webhooks.Receiver:" + path;
        if (builder.Services.Any(d => d.ServiceType == typeof(ReceiverMarker) && Equals(d.ImplementationInstance, new ReceiverMarker(path))))
            throw new InvalidOperationException($"A webhook receiver is already registered at '{path}'.");
        builder.Services.AddSingleton(new ReceiverMarker(path));

        var types = new Dictionary<string, Type>(eventTypes, StringComparer.Ordinal);
        builder.Services.AddWebhookReceiver(name, configure);
        builder.Registry.AddEndpoints(endpoints => endpoints.MapWebhookReceiver(path, name, services => new SparkWebhookBroadcaster(
            path,
            types,
            services.GetService<IMessageBus>() ?? throw new InvalidOperationException("The Spark webhook receiver needs Messaging: call spark.AddMessaging()."),
            services.GetRequiredService<IMessageRecipientRegistry>())));
        return builder;
    }

    private sealed record ReceiverMarker(string Path);
}

/// <summary>Verified events → durable messages, keyed on <c>webhook-id</c> (the GitHub receiver's W1 order: enqueue, then 2xx).</summary>
internal sealed class SparkWebhookBroadcaster : WebhookProcessor
{
    private static readonly MethodInfo BroadcastTyped = typeof(SparkWebhookBroadcaster).GetMethod(nameof(BroadcastAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> Closed = new();

    private readonly string receiver;
    private readonly IReadOnlyDictionary<string, Type> eventTypes;
    private readonly IMessageBus bus;
    private readonly IMessageRecipientRegistry recipients;

    // Built per request by the endpoint (its path and event map are not services), so no [Inject] here.
    public SparkWebhookBroadcaster(string receiver, IReadOnlyDictionary<string, Type> eventTypes, IMessageBus bus, IMessageRecipientRegistry recipients)
    {
        this.receiver = receiver;
        this.eventTypes = eventTypes;
        this.bus = bus;
        this.recipients = recipients;
    }

    protected override async Task DispatchAsync(WebhookContext context, CancellationToken cancellationToken)
    {
        // The key carries the receiver, so two producers that happen to reuse an id stay apart.
        var key = receiver + ":" + context.MessageId;
        if (eventTypes.TryGetValue(context.Type, out var dataType) && recipients.HasRecipient(typeof(WebhookMessage<>).MakeGenericType(dataType)))
        {
            var data = context.Data.Deserialize(dataType, SerializerOptions)
                ?? throw new JsonException($"Event '{context.Type}' has no data.");
            var method = Closed.GetOrAdd(dataType, t => BroadcastTyped.MakeGenericMethod(t));
            await (Task)method.Invoke(this, [context, data, key, cancellationToken])!;
            return;
        }

        if (!recipients.HasRecipient<UnknownWebhookMessage>())
            return;
        await bus.BroadcastOnceAsync(new UnknownWebhookMessage
        {
            Receiver = receiver,
            MessageId = context.MessageId,
            Type = context.Type,
            OccurredAt = context.Envelope.Timestamp,
            DataJson = context.Data.GetRawText(),
        }, key, cancellationToken);
    }

    private Task BroadcastAsync<T>(WebhookContext context, T data, string key, CancellationToken cancellationToken)
        => bus.BroadcastOnceAsync(new WebhookMessage<T>
        {
            Receiver = receiver,
            MessageId = context.MessageId,
            Type = context.Type,
            OccurredAt = context.Envelope.Timestamp,
            Data = data,
        }, key, cancellationToken);
}
