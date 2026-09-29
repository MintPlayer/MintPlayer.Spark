namespace MintPlayer.Spark.MailManager;

/// <summary>
/// Queues templated mail. Never sends inline: a request becomes a message on a Spark Messaging lane
/// (<c>mail-transactional</c> by default), and a worker renders and delivers it — so a relay outage
/// delays mail instead of failing the request that caused it, and a retry renders the same mail.
/// </summary>
/// <remarks>
/// Registered by <c>spark.AddMailManager()</c> (package <c>MintPlayer.Spark.MailManager</c>). A library
/// that sends mail references only this abstractions package and resolves <see cref="ISparkMailer"/>
/// optionally: absent, there is no mail transport in the app.
/// </remarks>
public interface ISparkMailer
{
    /// <summary>
    /// Queues one mail. The recipient's culture is resolved <b>now</b> (explicit
    /// <see cref="SparkMailRequest.Culture"/>, else the recipient's stored preference, else
    /// <c>Spark:Mail:DefaultCulture</c>) and stored on the message, so every attempt renders the same
    /// language. A suppressed recipient is not queued (<see cref="SparkMailReceipt.Suppressed"/>).
    /// </summary>
    Task<SparkMailReceipt> SendAsync(SparkMailRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a campaign: one message that fans out into one bulk mail per recipient on the
    /// <c>mail-bulk</c> lane, deduplicated per <c>{campaignId}:{recipient}</c> — never one mail with many
    /// BCCs. Queuing the same campaign id twice sends each recipient one mail.
    /// </summary>
    Task SendCampaignAsync(SparkMailCampaign campaign, CancellationToken cancellationToken = default);
}

/// <summary>One mail to queue.</summary>
public sealed record SparkMailRequest
{
    /// <summary>
    /// The template name, e.g. <c>Welcome</c> or <c>SparkAuth/PasswordReset</c>. Resolved per culture:
    /// <c>{name}.{culture}.mjml</c> → <c>{name}.{language}.mjml</c> → <c>{name}.mjml</c>.
    /// </summary>
    public required string Template { get; init; }

    /// <summary>The recipient's address.</summary>
    public required string To { get; init; }

    /// <summary>The recipient's display name, if any.</summary>
    public string? ToName { get; init; }

    /// <summary>
    /// An explicit culture (<c>nl-BE</c>, <c>fr</c>). Wins over the recipient's stored preference. Never
    /// taken from the ambient request culture — mail renders on a worker, where that means nothing.
    /// </summary>
    public string? Culture { get; init; }

    /// <summary>
    /// The template's data: any object that serializes to a JSON object. Its members are the
    /// template's variables (string values are HTML-escaped before rendering).
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// The data carries a secret (a reset or confirmation token, a sign-in link): it is encrypted with
    /// Data Protection in the queued message and the payload is scrubbed once the message is
    /// completed or dead-lettered (#460, T4). Set it for every mail carrying a token or a link that
    /// grants something.
    /// </summary>
    public bool Sensitive { get; init; }

    /// <summary>
    /// After this instant the mail is not sent — it dead-letters as <c>Expired</c>. Set it to a little
    /// before the token in the mail expires: a throttled reset mail must not arrive dead.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>Delay before the first attempt.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>At most one mail per key is queued within the messaging retention window.</summary>
    public string? DeduplicationKey { get; init; }

    /// <summary>
    /// The lane, overriding <c>mail-transactional</c>. Must be a queue declared in
    /// <c>Spark:Messaging:Queues</c> (or in code); an undeclared name is refused at publish.
    /// </summary>
    public string? Queue { get; init; }

    /// <summary>
    /// The unsubscribe stream (the suppression scope an unsubscribe adds). Defaults to the template
    /// name. A hard bounce suppresses every stream.
    /// </summary>
    public string? Stream { get; init; }

    /// <summary>
    /// Adds one-click <c>List-Unsubscribe</c> (RFC 8058) for <see cref="Stream"/>. Default: off for
    /// transactional mail, on for bulk.
    /// </summary>
    public bool? ListUnsubscribe { get; init; }
}

/// <summary>What queuing a mail produced.</summary>
/// <param name="DeliveryId">
/// The delivery's id: the VERP bounce address, the <c>Message-ID</c> and the
/// <c>SparkMailDeliveries/{id}</c> record all carry it.
/// </param>
/// <param name="Suppressed">The recipient is on the suppression list; nothing was queued.</param>
public sealed record SparkMailReceipt(string DeliveryId, bool Suppressed);

/// <summary>A bulk send: one template, many recipients, each mailed separately.</summary>
public sealed record SparkMailCampaign
{
    /// <summary>A stable id. Re-queuing the same id does not mail a recipient twice.</summary>
    public required string CampaignId { get; init; }

    /// <summary>The template name.</summary>
    public required string Template { get; init; }

    /// <summary>The recipients.</summary>
    public required IReadOnlyList<SparkMailCampaignRecipient> Recipients { get; init; }

    /// <summary>Data shared by every recipient; a recipient's own <see cref="SparkMailCampaignRecipient.Data"/> members win.</summary>
    public object? Data { get; init; }

    /// <summary>The unsubscribe stream; defaults to the template name.</summary>
    public string? Stream { get; init; }

    /// <summary>After this instant nothing more of the campaign is sent.</summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>One-click <c>List-Unsubscribe</c>; default on.</summary>
    public bool ListUnsubscribe { get; init; } = true;
}

/// <summary>One campaign recipient.</summary>
public sealed record SparkMailCampaignRecipient
{
    /// <summary>The address.</summary>
    public required string Email { get; init; }

    /// <summary>The display name.</summary>
    public string? Name { get; init; }

    /// <summary>An explicit culture for this recipient.</summary>
    public string? Culture { get; init; }

    /// <summary>Per-recipient data, merged over the campaign's.</summary>
    public object? Data { get; init; }
}
