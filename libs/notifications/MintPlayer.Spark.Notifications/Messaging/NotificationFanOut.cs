using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Notifications.Services;

namespace MintPlayer.Spark.Notifications.Messaging;

/// <summary>
/// What a notification sends once the caller's save has committed it: the pushes to each user's devices and the mails
/// (#536 D50). <see cref="ISparkNotifier.NotifyAsync"/> writes it to the outbox in the caller's session, so a
/// notification that rolls back sends nothing, and reads no device or account for it: the caller's session, whose
/// request budget it shares, pays one request per call, not two per user.
/// </summary>
[MessageQueue(SparkNotificationQueues.FanOut)]
public sealed class NotificationFanOutMessage
{
    /// <summary>Names this fan-out in the mails' deduplication keys, so a retried fan-out queues no mail twice.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    /// <summary>The notification's URL as given: app-relative or absolute.</summary>
    public string? Url { get; set; }

    /// <summary><see cref="Url"/> made absolute with the public base URL, resolved by the notifier (it may need the request); null when there is none.</summary>
    public string? Link { get; set; }

    public string? Category { get; set; }

    public bool Urgent { get; set; }

    public List<NotificationFanOutRecipient> Recipients { get; set; } = [];
}

/// <summary>One user of a <see cref="NotificationFanOutMessage"/>, with the channels their preference chose.</summary>
public sealed class NotificationFanOutRecipient
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>The user's in-app notification, which a push opens; null when in-app is off.</summary>
    public string? NotificationId { get; set; }

    public bool Push { get; set; }

    public bool Mail { get; set; }
}

/// <summary>
/// Sends a <see cref="NotificationFanOutMessage"/>. The mails come first, each with a key of this fan-out and user, so
/// a retry queues none twice. Then one <see cref="WebPushDeliveryMessage"/> per device goes to the outbox, saved in one
/// commit. The accounts are read again: an account deleted or unconfirmed since gets no mail.
/// </summary>
internal sealed partial class NotificationFanOutHandler : IRecipient<NotificationFanOutMessage>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly SparkWebPushSender push;
    [Inject] private readonly IOptions<SparkNotificationsOptions> options;
    [Inject] private readonly ILogger<NotificationFanOutHandler> logger;
    [Inject] private readonly ISparkAccountDirectory? directory;
    [Inject] private readonly ISparkMailer? mailer;

    public async Task HandleAsync(NotificationFanOutMessage message, CancellationToken cancellationToken = default)
    {
        var mailTo = message.Recipients.Where(r => r.Mail).Select(r => r.UserId).ToList();
        if (mailTo.Count > 0)
            await MailAsync(message, mailTo, cancellationToken);

        var pushTo = message.Recipients.Where(r => r.Push).ToList();
        if (pushTo.Count == 0)
            return;
        if (push.Client is null)
        {
            logger.LogDebug("Web Push is no longer configured: the pushes of {Count} users skipped.", pushTo.Count);
            return;
        }

        // One read per user (their devices) on this message's own session: the users bound the requests, not the budget.
        using var budget = session.IgnoreMaxRequests();
        var timeToLive = (long)options.Value.WebPush.TimeToLive.TotalSeconds;
        var topic = SparkNotifier.Topic(message.Category);
        foreach (var recipient in pushTo)
        {
            var payload = SparkWebPushSender.Payload(recipient.NotificationId, message.Title, message.Body, message.Link ?? message.Url, message.Category);
            var devices = await session.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(recipient.UserId), pageSize: 64, cancellationToken: cancellationToken);
            foreach (var device in devices)
            {
                await outbox.EnqueueAsync(new WebPushDeliveryMessage
                {
                    SubscriptionId = device.Id.Value,
                    Payload = payload,
                    Topic = topic,
                    Urgent = message.Urgent,
                    TimeToLiveSeconds = timeToLive,
                }, cancellationToken: cancellationToken);
            }
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task MailAsync(NotificationFanOutMessage message, List<string> userIds, CancellationToken cancellationToken)
    {
        if (mailer is null || directory is null)
        {
            logger.LogDebug("No MailManager or account directory: {Count} notification mails skipped.", userIds.Count);
            return;
        }
        var accounts = await directory.FindAsync(userIds, cancellationToken);
        foreach (var userId in userIds)
        {
            if (!accounts.TryGetValue(userId, out var account) || !account.EmailConfirmed || string.IsNullOrWhiteSpace(account.Email))
                continue;
            await mailer.SendAsync(new SparkMailRequest
            {
                Template = options.Value.MailTemplate,
                To = account.Email,
                ToName = account.UserName,
                Culture = account.PreferredCulture,
                Data = new { title = message.Title, body = message.Body, link = message.Link, user_name = account.UserName },
                DeduplicationKey = $"spark-notification:{message.Id}:{userId}",
            }, cancellationToken);
        }
    }
}
