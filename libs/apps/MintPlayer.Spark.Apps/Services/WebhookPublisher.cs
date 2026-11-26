using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Messaging;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Messaging;
using MintPlayer.Webhooks;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Apps;

/// <summary>
/// Publishes webhook events. Call it in the session of the business write: the event is an outbox message saved by
/// that <c>SaveChanges</c>, so it goes out exactly when the write commits, and never when it rolls back.
/// <code>
/// session.Store(report);
/// await webhooks.PublishAsync(new ReportCompleted(report.Id, report.LineRate), WebhookOwner.Organization(orgId), session);
/// await session.SaveChangesAsync();
/// </code>
/// </summary>
public interface IWebhookPublisher
{
    /// <summary>
    /// Queues <paramref name="data"/> (a declared <c>[WebhookEvent]</c>) for every endpoint of <paramref name="owner"/>
    /// that subscribes to it. Returns the event's <c>webhook-id</c>, or null when the owner has no endpoint at all.
    /// </summary>
    Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, IAsyncDocumentSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// <see cref="PublishAsync{TEvent}(TEvent, WebhookOwner, IAsyncDocumentSession, CancellationToken)"/> for a publisher that
    /// may run twice for one event, e.g. a message handler (at least once). The <c>webhook-id</c> is derived from
    /// <paramref name="idempotencyKey"/> (with the event type and owner), so a second run delivers nothing twice.
    /// </summary>
    Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, string idempotencyKey, IAsyncDocumentSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull;
}

// Stateless, so the interface and the concrete type (the entity events' fixed-id overload) may be separate instances.
[Register(typeof(IWebhookPublisher), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class WebhookPublisher : IWebhookPublisher
{
    [Inject] private readonly WebhookEventCatalog catalog;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly TimeProvider time;

    public Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, IAsyncDocumentSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull
        => PublishAsync(data, owner, session, messageId: null, cancellationToken);

    public Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, string idempotencyKey, IAsyncDocumentSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        return PublishAsync(data, owner, session, KeyedMessageId(data.GetType(), owner, idempotencyKey), cancellationToken);
    }

    /// <summary>A <c>msg_</c> id that is the same for the same (event type, owner, key): no time prefix, so a rerun matches.</summary>
    internal static string KeyedMessageId(Type eventType, WebhookOwner owner, string key)
    {
        var input = Encoding.UTF8.GetBytes($"{eventType.FullName}|{owner.Kind.Length}:{owner.Kind}|{owner.Key.Length}:{owner.Key}|{key}");
        return "msg_" + Convert.ToHexStringLower(SHA256.HashData(input).AsSpan(0, 16));
    }

    /// <summary>
    /// <paramref name="messageId"/>: a fixed <c>webhook-id</c> for a publisher that may run twice for one event (the
    /// at-least-once entity events, D31). The fan-out derives delivery ids from it, so a second run delivers nothing twice.
    /// </summary>
    internal async Task<string?> PublishAsync(object data, WebhookOwner owner, IAsyncDocumentSession session, string? messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var descriptor = catalog.Find(data.GetType())
            ?? throw new InvalidOperationException($"{data.GetType().Name} is not a webhook event: mark it [WebhookEvent] and register it with AddApps(apps => apps.AddEvent…).");

        // The owner's registry lists its destinations; an owner without one costs a single load and no message.
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(owner).Value, cancellationToken);
        if (registry is not { HasDestinations: true })
            return null;

        var now = time.GetUtcNow();
        if (messageId is not null)
        {
            // A fixed id was published before: the first run's message stands. Two runs at once both get here; the
            // create-only store (empty change vector) fails the second SaveChanges, and its retry sees the key.
            var keyId = WebhookEventKey.IdFor(messageId).Value;
            if (await session.LoadAsync<WebhookEventKey>(keyId, cancellationToken) is not null)
                return messageId;
            await session.StoreAsync(new WebhookEventKey { ExpiresAtUtc = now.UtcDateTime + options.Value.DeliveryRetention }, string.Empty, keyId, cancellationToken);
        }

        var message = new WebhookEventRaised
        {
            MessageId = messageId ?? NewMessageId(now),
            EventType = descriptor.Name,
            OwnerKind = owner.Kind,
            OwnerKey = owner.Key,
            Payload = Envelope(descriptor.Name, now, JsonSerializer.SerializeToNode(data, data.GetType(), WebhookJson.Options), options.Value.MaxPayloadBytes),
            CreatedUtc = now.UtcDateTime,
        };
        await outbox.EnqueueAsync(session, message, cancellationToken: cancellationToken);
        return message.MessageId;
    }

    /// <summary>A Standard Webhooks <c>msg_</c> id: time-ordered, then random.</summary>
    internal static string NewMessageId(DateTimeOffset now)
        => "msg_" + now.ToUnixTimeMilliseconds().ToString("x12") + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));

    /// <summary>
    /// The envelope. Over <paramref name="maxBytes"/> it goes out thin (D22): <c>data</c> is replaced by
    /// <c>{ "truncated": true, "size": n }</c> and the receiver reads the object through the API.
    /// </summary>
    internal static string Envelope(string type, DateTimeOffset timestamp, JsonNode? data, int maxBytes)
    {
        var json = Serialize(type, timestamp, data);
        var size = Encoding.UTF8.GetByteCount(json);
        return size <= maxBytes
            ? json
            : Serialize(type, timestamp, new JsonObject { ["truncated"] = true, ["size"] = size });
    }

    private static string Serialize(string type, DateTimeOffset timestamp, JsonNode? data) => new JsonObject
    {
        ["type"] = type,
        ["timestamp"] = timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["data"] = data,
    }.ToJsonString(WebhookJson.Options);
}
