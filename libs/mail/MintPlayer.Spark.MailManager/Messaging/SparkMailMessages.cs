using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.MailManager.Messaging;

/// <summary>The lanes MailManager declares (<c>Spark:Messaging:Queues:{name}</c> overrides each).</summary>
public static class SparkMailQueues
{
    /// <summary>One-to-one mail: account mail, notifications. Generous: no throttle, long retry.</summary>
    public const string Transactional = "mail-transactional";

    /// <summary>Campaign mail. Strict: throttled (GCRA) so a campaign cannot starve the relay.</summary>
    public const string Bulk = "mail-bulk";
}

/// <summary>
/// One queued mail. Everything the send needs travels in the message — the resolved culture above
/// all, so a retry renders the same language however the recipient's preference changed meanwhile.
/// </summary>
[MessageQueue(SparkMailQueues.Transactional)]
public sealed class SparkMailMessage
{
    /// <summary>The delivery id (VERP, <c>Message-ID</c>, <c>SparkMailDeliveries/{id}</c>).</summary>
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>The template name.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The recipient.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>The recipient's display name.</summary>
    public string? ToName { get; set; }

    /// <summary>The culture resolved when the mail was queued (<c>nl-BE</c>; empty = invariant).</summary>
    public string Culture { get; set; } = string.Empty;

    /// <summary>The template data as JSON, or <c>sdp1:</c> + its Data Protection ciphertext (T4).</summary>
    public string? Data { get; set; }

    /// <summary>The unsubscribe / suppression stream.</summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>Adds one-click <c>List-Unsubscribe</c>.</summary>
    public bool ListUnsubscribe { get; set; }

    /// <summary>The campaign this mail belongs to, if any.</summary>
    public string? CampaignId { get; set; }

    /// <summary>The lane it was queued on (<see cref="SparkMailQueues"/>); null (a message queued by an older version) means transactional. Informational: handed to the transport as <see cref="Transports.SparkMailSendContext.Lane"/>.</summary>
    public string? Queue { get; set; }
}

/// <summary>A queued campaign; its handler fans it out into one <see cref="SparkMailMessage"/> per recipient.</summary>
[MessageQueue(SparkMailQueues.Bulk)]
public sealed class SparkMailCampaignMessage
{
    /// <summary>The campaign id.</summary>
    public string CampaignId { get; set; } = string.Empty;

    /// <summary>The template.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The stream.</summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>Shared data as JSON.</summary>
    public string? Data { get; set; }

    /// <summary>One-click unsubscribe.</summary>
    public bool ListUnsubscribe { get; set; } = true;

    /// <summary>After this nothing more is sent.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The recipients.</summary>
    public List<SparkMailCampaignMessageRecipient> Recipients { get; set; } = [];
}

/// <summary>A campaign recipient on the wire.</summary>
public sealed class SparkMailCampaignMessageRecipient
{
    /// <summary>The address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    public string? Name { get; set; }

    /// <summary>The explicit culture, if any.</summary>
    public string? Culture { get; set; }

    /// <summary>Per-recipient data as JSON.</summary>
    public string? Data { get; set; }
}
