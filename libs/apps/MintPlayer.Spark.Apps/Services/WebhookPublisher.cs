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
}

internal sealed partial class WebhookPublisher : IWebhookPublisher
{
    [Inject] private readonly WebhookEventCatalog catalog;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly TimeProvider time;

    public async Task<string?> PublishAsync<TEvent>(TEvent data, WebhookOwner owner, IAsyncDocumentSession session, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(session);
        var descriptor = catalog.Find(data.GetType())
            ?? throw new InvalidOperationException($"{data.GetType().Name} is not a webhook event: mark it [WebhookEvent] and register it with AddApps(apps => apps.AddEvent…).");

        // The owner's registry lists its destinations; an owner without one costs a single load and no message.
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(owner), cancellationToken);
        if (registry is null || registry.EndpointIds.Count == 0)
            return null;

        var now = time.GetUtcNow();
        var message = new WebhookEventRaised
        {
            MessageId = NewMessageId(now),
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
