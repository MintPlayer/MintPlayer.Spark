using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
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
/// <para>
/// Writes in the scope's session and never saves (#536 D50): the notifications and their
/// <see cref="NotificationFanOutMessage"/> commit with the caller's <c>SaveChanges</c>, and a <c>Clear()</c> before a
/// retry drops them. The caller's session pays one request per call: every user's preferences in one load.
/// </para>
/// </remarks>
internal sealed partial class SparkNotifier : ISparkNotifier
{
    internal const int MaxTitle = 200;
    internal const int MaxBody = 2000;

    [Inject] private readonly ISparkSession session;
    [Inject] private readonly SparkWebPushSender push;
    [Inject] private readonly IOptions<SparkNotificationsOptions> options;
    [Inject] private readonly IOptions<SparkVisitorOptions> visitorOptions;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<SparkNotifier> logger;
    [Inject] private readonly IMessageOutbox? outbox;
    [Inject] private readonly ISparkAccountDirectory? directory;
    [Inject] private readonly ISparkMailer? mailer;
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;
    [Inject] private readonly IConfiguration? configuration;
    [Inject] private readonly IHostEnvironment? environment;

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
        var userIds = Clean(notification.UserIds);
        // A visitor id always carries its prefix, so one can never address a user's notifications.
        var visitorIds = Clean(notification.VisitorIds).Where(id => id.StartsWith(SparkVisitorOptions.IdPrefix, StringComparison.Ordinal)).ToList();

        // With a directory, an unknown id is skipped; without one, the id is taken at its word (in-app only).
        IReadOnlyDictionary<string, ISparkAccount>? accounts = null;
        if (userIds.Count > 0 && directory is not null)
        {
            var found = await directory.FindAsync(userIds, cancellationToken);
            userIds = userIds.Where(found.ContainsKey).ToList();
            accounts = found;
        }

        // Pushes and mails go out from the fan-out message, which needs the outbox (Messaging; MailManager requires it).
        var canPush = push.Client is not null && outbox is not null;
        var canMail = mailer is not null && outbox is not null;
        var fanOut = new List<NotificationFanOutRecipient>();
        var mails = 0;
        var pushes = 0;
        var channels = await ChannelsAsync(userIds, notification.OrganizationId, cancellationToken);
        foreach (var userId in userIds)
        {
            var chosen = channels[userId];
            string? notificationId = null;
            if (chosen.InApp)
            {
                var stored = NewNotification(notification, title, now);
                stored.UserId = userId;
                // No ExpiresAtUtc: the library model's "expiresAfter" (90 days) stamps it (X1).
                await session.StoreAsync(stored, stored.Id.Value, cancellationToken);
                notificationId = stored.Id.Value;
                ids.Add(notificationId);
            }

            if (accounts?.GetValueOrDefault(userId) is not { } account)
                continue;
            var pushIt = chosen.Push && canPush;
            var mailIt = chosen.Email && account.EmailConfirmed && !string.IsNullOrWhiteSpace(account.Email);
            if (mailIt && !canMail)
            {
                logger.LogDebug("No MailManager registered: the notification mail to {UserId} skipped.", userId);
                mailIt = false;
            }
            if (!pushIt && !mailIt)
                continue;
            fanOut.Add(new NotificationFanOutRecipient { UserId = userId, NotificationId = notificationId, Push = pushIt, Mail = mailIt });
            pushes += pushIt ? 1 : 0;
            mails += mailIt ? 1 : 0;
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

        if (fanOut.Count > 0)
        {
            await outbox!.EnqueueAsync(new NotificationFanOutMessage
            {
                Id = Guid.CreateVersion7().ToString("N"),
                Title = title,
                Body = notification.Body,
                Url = notification.Url,
                Link = Absolute(notification.Url),
                Category = notification.Category,
                Urgent = notification.Urgent,
                Recipients = fanOut,
            }, cancellationToken: cancellationToken);
        }

        return new SparkNotifyReceipt(ids, mails, pushes);
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

    /// <summary>
    /// Each user's preference for the organization, else their default, else the configured defaults: one load for
    /// every user, since the caller's session (and its request budget) is shared.
    /// </summary>
    internal async Task<Dictionary<string, SparkNotificationChannels>> ChannelsAsync(IReadOnlyCollection<string> userIds, string? organizationId, CancellationToken cancellationToken)
    {
        var channels = new Dictionary<string, SparkNotificationChannels>(StringComparer.OrdinalIgnoreCase);
        if (userIds.Count == 0)
            return channels;
        var keys = userIds.Select(userId => (UserId: userId, Scoped: NotificationPreference.IdFor(userId, organizationId).Value, Fallback: NotificationPreference.IdFor(userId, null).Value)).ToList();
        var found = await session.LoadAsync<NotificationPreference>(keys.SelectMany(k => new[] { k.Scoped, k.Fallback }).Distinct(), cancellationToken);
        foreach (var (userId, scoped, fallback) in keys)
        {
            var preference = (found.TryGetValue(scoped, out var s) ? s : null) ?? (found.TryGetValue(fallback, out var f) ? f : null);
            channels[userId] = preference is null
                ? options.Value.Defaults
                : new SparkNotificationChannels { InApp = preference.InApp, Email = preference.Email, Push = preference.Push };
        }
        return channels;
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
