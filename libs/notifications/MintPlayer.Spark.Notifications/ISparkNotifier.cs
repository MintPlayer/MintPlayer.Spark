namespace MintPlayer.Spark.Notifications;

/// <summary>
/// Notifies users (<c>docs/seo_analyzer_PRD.md</c> X10, D20): one call, three channels, chosen per user and
/// organization by their <see cref="Models.NotificationPreference"/>:
/// <list type="bullet">
/// <item><b>in-app</b>: a <see cref="Models.Notification"/> document, which reaches an open UI over the live socket
/// (<c>/spark/live</c>, X4) because the type and its queries are <c>"live": true</c>;</item>
/// <item><b>email</b>: one mail through MailManager (<c>SparkNotifications/Notification</c>, en + nl);</item>
/// <item><b>Web Push</b>: one durable message per subscribed device, sent by the <c>spark-webpush</c> queue.</item>
/// </list>
/// </summary>
/// <remarks>
/// The documents and the push messages are committed in one <c>SaveChanges</c> (the Messaging outbox), so a
/// failure stores nothing. Mails are queued by MailManager afterwards (its own durable queue). A channel that is
/// not set up (no MailManager, no VAPID keys) is skipped, never an error.
/// </remarks>
public interface ISparkNotifier
{
    Task<SparkNotifyReceipt> NotifyAsync(SparkNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>What to tell whom.</summary>
public sealed record SparkNotification
{
    /// <summary>The users (<c>SparkUsers/…</c>). Unknown ids are skipped.</summary>
    public required IReadOnlyCollection<string> UserIds { get; init; }

    /// <summary>The headline: the list's first line, the push title, the mail subject. At most 200 characters.</summary>
    public required string Title { get; init; }

    /// <summary>The text, at most 2 000 characters (a push carries the first 1 000).</summary>
    public string? Body { get; init; }

    /// <summary>Where a click goes: an app-relative path (<c>/runs/42</c>). Mails and pushes make it absolute with the public base URL.</summary>
    public string? Url { get; init; }

    /// <summary>The organization it is about: picks each user's preference for it.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>An application-defined kind (<c>score-drop</c>). Also the push <c>Topic</c>, so a newer push of the same kind replaces an undelivered one.</summary>
    public string? Category { get; init; }

    /// <summary>Push urgency: <c>true</c> sends <c>Urgency: high</c>.</summary>
    public bool Urgent { get; init; }
}

/// <summary>What was done.</summary>
/// <param name="NotificationIds">The in-app notifications stored.</param>
/// <param name="Mails">Mails queued.</param>
/// <param name="Pushes">Push messages queued (one per device).</param>
public sealed record SparkNotifyReceipt(IReadOnlyList<string> NotificationIds, int Mails, int Pushes);
