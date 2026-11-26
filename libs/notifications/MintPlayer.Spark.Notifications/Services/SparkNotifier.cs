using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications.Messaging;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Notifications.Services;

/// <remarks>
/// Knows no user type (<c>docs/notification_bell_PRD.md</c> G4): accounts come from the optional
/// <see cref="ISparkAccountDirectory"/>. Without one, users get in-app notifications only, since mail and push
/// need an account to address. Visitors (<see cref="SparkNotification.VisitorIds"/>) get in-app only, always,
/// expiring with the visitor (<see cref="SparkVisitorOptions.Lifetime"/>).
/// </remarks>
internal sealed class SparkNotifier(
    IServiceProvider services,
    SparkWebPushSender push,
    IOptions<SparkNotificationsOptions> options,
    IOptions<SparkVisitorOptions> visitorOptions,
    TimeProvider timeProvider,
    ILogger<SparkNotifier> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : ISparkNotifier
{
    internal const int MaxTitle = 200;
    internal const int MaxBody = 2000;

    public async Task<SparkNotifyReceipt> NotifyAsync(SparkNotification notification, CancellationToken cancellationToken = default)
    {
        var title = notification.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            throw new ArgumentException("A notification needs a title.", nameof(notification));
        if (title.Length > MaxTitle)
            throw new ArgumentException($"A notification title is at most {MaxTitle} characters.", nameof(notification));
        if (notification.Body is { Length: > MaxBody })
            throw new ArgumentException($"A notification body is at most {MaxBody} characters.", nameof(notification));
        if (notification.Url is { } url && !SparkLinkUrl.IsAllowed(url))
            throw new ArgumentException($"A notification URL is {SparkLinkUrl.AllowedForms}.", nameof(notification));

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = new List<string>();
        var mails = new List<ISparkAccount>();
        var pushes = 0;

        // The notifications commit on their own, whatever the caller has pending: a unit of work of its own, so a scope
        // of its own (#536 D50), whose session the push outbox writes to as well.
        await using var scope = services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<ISparkSession>();
        var outbox = scope.ServiceProvider.GetService<IMessageOutbox>();
        var userIds = Clean(notification.UserIds);
        // A visitor id always carries its prefix, so one can never address a user's notifications.
        var visitorIds = Clean(notification.VisitorIds).Where(id => id.StartsWith(SparkVisitorOptions.IdPrefix, StringComparison.Ordinal)).ToList();

        var directory = services.GetService<ISparkAccountDirectory>();
        IReadOnlyDictionary<string, ISparkAccount>? accounts = null;
        if (userIds.Count > 0 && directory is not null)
            accounts = await directory.FindAsync(userIds, cancellationToken);

        string? absoluteUrl = null;
        var absoluteResolved = false;
        string? AbsoluteUrl()
        {
            if (!absoluteResolved)
            {
                absoluteUrl = Absolute(notification.Url);
                absoluteResolved = true;
            }
            return absoluteUrl;
        }

        foreach (var userId in userIds)
        {
            // With a directory, an unknown id is skipped; without one, the id is taken at its word (in-app only).
            ISparkAccount? account = null;
            if (accounts is not null && !accounts.TryGetValue(userId, out account))
                continue;

            var channels = await ChannelsAsync(session, userId, notification.OrganizationId, cancellationToken);
            string? notificationId = null;
            if (channels.InApp)
            {
                var stored = NewNotification(notification, title, now);
                stored.UserId = userId;
                // No ExpiresAtUtc: the library model's "expiresAfter" (90 days) stamps it (X1).
                await session.StoreAsync(stored, stored.Id.Value, cancellationToken);
                notificationId = stored.Id.Value;
                ids.Add(notificationId);
            }

            if (account is null)
                continue;

            if (channels.Push && push.Client is not null && outbox is not null)
            {
                var payload = SparkWebPushSender.Payload(notificationId, title, notification.Body, AbsoluteUrl() ?? notification.Url, notification.Category);
                var devices = await session.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), pageSize: 64, cancellationToken: cancellationToken);
                foreach (var device in devices)
                {
                    await outbox.EnqueueAsync(new WebPushDeliveryMessage
                    {
                        SubscriptionId = device.Id.Value,
                        Payload = payload,
                        Topic = Topic(notification.Category),
                        Urgent = notification.Urgent,
                        TimeToLiveSeconds = (long)options.Value.WebPush.TimeToLive.TotalSeconds,
                    }, cancellationToken: cancellationToken);
                    pushes++;
                }
            }

            if (channels.Email && account.EmailConfirmed && !string.IsNullOrWhiteSpace(account.Email))
                mails.Add(account);
        }

        foreach (var visitorId in visitorIds)
        {
            var stored = NewNotification(notification, title, now);
            stored.VisitorId = visitorId;
            // Explicit, so the model's 90 days don't apply: the notification dies with the visitor.
            stored.ExpiresAtUtc = now + visitorOptions.Value.Lifetime;
            await session.StoreAsync(stored, stored.Id.Value, cancellationToken);
            ids.Add(stored.Id.Value);
        }

        await session.SaveChangesAsync(cancellationToken);

        var sent = 0;
        if (mails.Count > 0)
        {
            if (services.GetService<ISparkMailer>() is not { } mailer)
            {
                logger.LogDebug("No MailManager registered: {Count} notification mails skipped.", mails.Count);
            }
            else
            {
                var link = AbsoluteUrl();
                foreach (var account in mails)
                {
                    await mailer.SendAsync(new SparkMailRequest
                    {
                        Template = options.Value.MailTemplate,
                        To = account.Email!,
                        ToName = account.UserName,
                        Culture = account.PreferredCulture,
                        Data = new { title, body = notification.Body, link, user_name = account.UserName },
                    }, cancellationToken);
                    sent++;
                }
            }
        }

        return new SparkNotifyReceipt(ids, sent, pushes);
    }

    private static List<string> Clean(IReadOnlyCollection<string>? ids)
        => ids is null ? [] : ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Notification NewNotification(SparkNotification notification, string title, DateTime now) => new()
    {
        Id = Notification.NewId(),
        OrganizationId = notification.OrganizationId,
        Title = title,
        Body = notification.Body,
        Url = notification.Url,
        Category = notification.Category,
        CreatedAtUtc = now,
    };

    /// <summary>The user's preference for the organization, else their default, else the configured defaults.</summary>
    internal async Task<SparkNotificationChannels> ChannelsAsync(ISparkSession session, string userId, string? organizationId, CancellationToken cancellationToken)
    {
        var scoped = NotificationPreference.IdFor(userId, organizationId).Value;
        var fallback = NotificationPreference.IdFor(userId, null).Value;
        var found = await session.LoadAsync<NotificationPreference>([scoped, fallback], cancellationToken);
        var preference = (found.TryGetValue(scoped, out var s) ? s : null) ?? (found.TryGetValue(fallback, out var f) ? f : null);
        return preference is null
            ? options.Value.Defaults
            : new SparkNotificationChannels { InApp = preference.InApp, Email = preference.Email, Push = preference.Push };
    }

    private string? Absolute(string? url)
    {
        // Decided on the text (SparkLinkUrl), not by Uri.TryCreate(UriKind.Absolute): on Linux that accepts "/runs/7" as a file:// URI.
        if (!SparkLinkUrl.IsAppPath(url))
            return url;
        var origin = SparkPublicBaseUrl.Resolve(options.Value.PublicBaseUrl, configuration, environment, httpContextAccessor?.HttpContext);
        if (origin is null)
        {
            logger.LogWarning("Notification links stay relative: no public base URL is configured. Set 'Spark:Notifications:PublicBaseUrl' or 'Spark:Auth:PublicBaseUrl'.");
            return null;
        }
        return origin + url;
    }

    /// <summary>A category as an RFC 8030 topic (1–32 base64url characters), or none.</summary>
    internal static string? Topic(string? category)
    {
        if (string.IsNullOrEmpty(category))
            return null;
        var cleaned = new string(category.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? null : cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }
}
