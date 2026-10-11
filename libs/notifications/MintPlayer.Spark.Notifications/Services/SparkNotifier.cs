using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications.Messaging;
using MintPlayer.Spark.Notifications.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Notifications.Services;

internal sealed class SparkNotifier(
    IDocumentStore store,
    IServiceProvider services,
    SparkWebPushSender push,
    IOptions<SparkNotificationsOptions> options,
    IOptions<SparkAuthenticationOptions> authOptions,
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
        if (notification.Url is { } url && !IsAllowedUrl(url))
            throw new ArgumentException("A notification URL is an app-relative path ('/runs/42') or an absolute http(s) URL.", nameof(notification));

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var absoluteUrl = Absolute(notification.Url);
        var outbox = services.GetService<IMessageOutbox>();
        var ids = new List<string>();
        var mails = new List<(SparkUser User, string? Culture)>();
        var pushes = 0;

        using var session = store.OpenAsyncSession();
        var userIds = notification.UserIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var users = await session.LoadAsync<SparkUser>(userIds, cancellationToken);

        foreach (var userId in userIds)
        {
            if (!users.TryGetValue(userId, out var user) || user is null)
                continue;

            var channels = await ChannelsAsync(session, userId, notification.OrganizationId, cancellationToken);
            string? notificationId = null;
            if (channels.InApp)
            {
                var stored = new Notification
                {
                    Id = Notification.NewId(),
                    UserId = userId,
                    OrganizationId = notification.OrganizationId,
                    Title = title,
                    Body = notification.Body,
                    Url = notification.Url,
                    Category = notification.Category,
                    CreatedAtUtc = now,
                };
                // No ExpiresAtUtc: the library model's "expiresAfter" (90 days) stamps it (X1).
                await session.StoreAsync(stored, stored.Id, cancellationToken);
                notificationId = stored.Id!;
                ids.Add(notificationId);
            }

            if (channels.Push && push.Client is not null && outbox is not null)
            {
                var payload = SparkWebPushSender.Payload(notificationId, title, notification.Body, absoluteUrl ?? notification.Url, notification.Category);
                var devices = await session.Advanced.LoadStartingWithAsync<PushSubscription>(PushSubscription.PrefixFor(userId), pageSize: 64, token: cancellationToken);
                foreach (var device in devices)
                {
                    await outbox.EnqueueAsync(session, new WebPushDeliveryMessage
                    {
                        SubscriptionId = device.Id!,
                        Payload = payload,
                        Topic = Topic(notification.Category),
                        Urgent = notification.Urgent,
                        TimeToLiveSeconds = (long)options.Value.WebPush.TimeToLive.TotalSeconds,
                    }, cancellationToken: cancellationToken);
                    pushes++;
                }
            }

            if (channels.Email && user.EmailConfirmed && !string.IsNullOrWhiteSpace(user.Email))
                mails.Add((user, user.PreferredCulture));
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
                foreach (var (user, culture) in mails)
                {
                    await mailer.SendAsync(new SparkMailRequest
                    {
                        Template = options.Value.MailTemplate,
                        To = user.Email!,
                        ToName = user.UserName,
                        Culture = culture,
                        Data = new { title, body = notification.Body, link = absoluteUrl, user_name = user.UserName },
                    }, cancellationToken);
                    sent++;
                }
            }
        }

        return new SparkNotifyReceipt(ids, sent, pushes);
    }

    /// <summary>The user's preference for the organization, else their default, else the configured defaults.</summary>
    internal async Task<SparkNotificationChannels> ChannelsAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string userId, string? organizationId, CancellationToken cancellationToken)
    {
        var scoped = NotificationPreference.IdFor(userId, organizationId);
        var fallback = NotificationPreference.IdFor(userId, null);
        var found = await session.LoadAsync<NotificationPreference>([scoped, fallback], cancellationToken);
        var preference = (found.TryGetValue(scoped, out var s) ? s : null) ?? (found.TryGetValue(fallback, out var f) ? f : null);
        return preference is null
            ? options.Value.Defaults
            : new SparkNotificationChannels { InApp = preference.InApp, Email = preference.Email, Push = preference.Push };
    }

    private string? Absolute(string? url)
    {
        // Decided on the text, not by Uri.TryCreate(UriKind.Absolute): on Linux that accepts "/runs/7" as a file:// URI.
        if (url is null || !IsRootRelative(url))
            return url;
        var origin = SparkPublicBaseUrl.Resolve(authOptions.Value.Links.PublicBaseUrl, configuration, environment, httpContextAccessor?.HttpContext);
        if (origin is null)
        {
            logger.LogWarning("Notification links stay relative: no public base URL is configured. Set 'Spark:Auth:PublicBaseUrl'.");
            return null;
        }
        return origin + url;
    }

    internal static bool IsAllowedUrl(string url)
        => IsRootRelative(url)
           || (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp));

    /// <summary>A path on this app ("/x"), not a protocol-relative "//host" or the "/\host" browsers read as one.</summary>
    private static bool IsRootRelative(string url)
        => url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal);

    /// <summary>A category as an RFC 8030 topic (1–32 base64url characters), or none.</summary>
    internal static string? Topic(string? category)
    {
        if (string.IsNullOrEmpty(category))
            return null;
        var cleaned = new string(category.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? null : cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }
}
