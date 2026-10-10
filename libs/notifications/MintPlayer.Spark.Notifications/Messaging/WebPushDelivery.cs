using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Notifications.Services;
using MintPlayer.Spark.WebPush;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Notifications.Messaging;

/// <summary>The queue of Web Push deliveries: one message per device and notification.</summary>
public static class SparkNotificationQueues
{
    public const string WebPush = "spark-webpush";
}

/// <summary>One push to one device, written to the Messaging outbox in the notifier's <c>SaveChanges</c>.</summary>
[MessageQueue(SparkNotificationQueues.WebPush)]
public sealed class WebPushDeliveryMessage
{
    /// <summary>The <see cref="PushSubscription"/> document; a deleted one ends the delivery quietly.</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>The JSON the service worker reads (Angular's <c>{ "notification": { … } }</c> shape).</summary>
    public string Payload { get; set; } = string.Empty;

    public string? Topic { get; set; }

    public bool Urgent { get; set; }

    /// <summary>The push service keeps the message this long (seconds).</summary>
    public long TimeToLiveSeconds { get; set; }
}

/// <summary>
/// Sends a <see cref="WebPushDeliveryMessage"/> through the SSRF-safe client (X2). 404/410 deletes the
/// subscription; 429/5xx throws, so Messaging retries with the queue's backoff; any other refusal is a
/// configuration problem (VAPID keys, payload) and parks the message as failed.
/// </summary>
internal sealed class WebPushDeliveryHandler(
    IDocumentStore store,
    SparkWebPushSender sender,
    ILogger<WebPushDeliveryHandler> logger) : IRecipient<WebPushDeliveryMessage>
{
    public async Task HandleAsync(WebPushDeliveryMessage message, CancellationToken cancellationToken = default)
    {
        if (sender.Client is not { } client)
            throw new NonRetryableException("Web Push is not configured (Spark:Notifications:WebPush).");

        PushSubscription? subscription;
        using (var session = store.OpenAsyncSession())
            subscription = await session.LoadAsync<PushSubscription>(message.SubscriptionId, cancellationToken);
        if (subscription is null)
            return; // unsubscribed meanwhile

        var result = await client.SendAsync(
            new WebPushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth),
            new WebPushMessage
            {
                Payload = System.Text.Encoding.UTF8.GetBytes(message.Payload),
                TimeToLive = TimeSpan.FromSeconds(message.TimeToLiveSeconds),
                Topic = message.Topic,
                Urgency = message.Urgent ? WebPushUrgency.High : null,
            },
            cancellationToken);

        switch (result.Outcome)
        {
            case WebPushOutcome.Delivered:
                return;
            case WebPushOutcome.SubscriptionGone:
                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(message.SubscriptionId);
                    await session.SaveChangesAsync(cancellationToken);
                }
                logger.LogInformation("Push subscription {SubscriptionId} is gone ({Status}); deleted.", message.SubscriptionId, (int)result.StatusCode);
                return;
            case WebPushOutcome.RetryLater:
                throw new HttpRequestException($"The push service answered {(int)result.StatusCode}; retrying (Retry-After {result.RetryAfter?.TotalSeconds.ToString() ?? "none"}).");
            default:
                throw new NonRetryableException($"The push service refused the message with {(int)result.StatusCode}: {result.Reason}");
        }
    }
}

/// <summary>The one <see cref="WebPushClient"/> of the application, null while VAPID is not configured.</summary>
internal sealed class SparkWebPushSender : IDisposable
{
    private readonly VapidAuthorization? vapid;

    public SparkWebPushSender(IOptions<SparkNotificationsOptions> options, IHttpClientFactory httpClients, TimeProvider timeProvider)
    {
        var push = options.Value.WebPush;
        if (!push.IsConfigured)
            return;
        vapid = new VapidAuthorization(new VapidDetails(push.PublicKey!.Trim(), push.PrivateKey!.Trim(), push.Subject!.Trim()), timeProvider);
        Client = new WebPushClient(httpClients.CreateClient(SparkNotificationsExtensions.WebPushHttpClient), vapid);
    }

    public WebPushClient? Client { get; }

    public void Dispose() => vapid?.Dispose();

    /// <summary>
    /// The payload Angular's service worker (<c>provideSparkServiceWorker()</c>) shows and acts on by itself:
    /// <c>notification.title/body/tag</c>, and <c>data.onActionClick.default</c> opening <c>url</c> (focusing
    /// an open tab of the app first).
    /// </summary>
    internal static string Payload(string? notificationId, string title, string? body, string? url, string? tag)
    {
        var bodyText = body is { Length: > 1000 } ? body[..1000] : body;
        string Build(string? text)
        {
            var data = new JsonObject();
            if (notificationId is not null)
                data["notificationId"] = notificationId;
            if (url is not null)
            {
                data["url"] = url;
                data["onActionClick"] = new JsonObject
                {
                    ["default"] = new JsonObject { ["operation"] = "navigateLastFocusedOrOpen", ["url"] = url },
                };
            }
            var notification = new JsonObject { ["title"] = title, ["data"] = data };
            if (text is not null)
                notification["body"] = text;
            if (tag is not null)
                notification["tag"] = tag;
            return new JsonObject { ["notification"] = notification }.ToJsonString();
        }

        var json = Build(bodyText);
        // A push body is at most 3 993 bytes before encryption; shorten the text rather than lose the push.
        while (System.Text.Encoding.UTF8.GetByteCount(json) > WebPushMessage.MaxPayloadBytes && bodyText is { Length: > 0 })
        {
            bodyText = bodyText[..(bodyText.Length / 2)];
            json = Build(bodyText);
        }
        return json;
    }
}
