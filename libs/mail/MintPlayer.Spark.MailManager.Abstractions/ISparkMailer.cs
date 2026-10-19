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

    /// <summary>
    /// Files attached to the mail: inline bytes (<see cref="SparkMailAttachment.FromBytes"/>, for small generated files —
    /// a receipt, an invoice, an <c>.ics</c>) or a RavenDB attachment read when the mail is sent
    /// (<see cref="SparkMailAttachment.FromDocument"/>, for stored files such as a legal document's PDF). Inline bytes
    /// travel in the queued message (protected and scrubbed like <see cref="Data"/> when <see cref="Sensitive"/>), so
    /// they are capped by <c>Spark:Mail:Attachments:MaxInlineBytes</c>; the whole mail by
    /// <c>Spark:Mail:Attachments:MaxTotalBytes</c>.
    /// </summary>
    public IReadOnlyList<SparkMailAttachment>? Attachments { get; init; }

    /// <summary>
    /// Extra headers, e.g. the <c>List-Unsubscribe</c> / <c>List-Unsubscribe-Post</c> pair of the consent primitive
    /// (<c>ISparkConsentLinks.CreateListUnsubscribeHeaders</c>, RFC 2369 + RFC 8058) on a marketing mail. A header given
    /// here replaces MailManager's own of the same name (so a consent <c>List-Unsubscribe</c> wins over the stream one
    /// <see cref="ListUnsubscribe"/> adds). Refused at queue time: a name that is not an RFC 5322 field name, a value
    /// with a line break (header injection), and the headers MailManager owns (<see cref="SparkMailHeaders.Reserved"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>A file attached to a mail. Exactly one source: <see cref="Content"/> or <see cref="DocumentId"/> + <see cref="AttachmentName"/>.</summary>
public sealed record SparkMailAttachment
{
    /// <summary>The file name the recipient sees (no path; line breaks and path separators are refused).</summary>
    public required string FileName { get; init; }

    /// <summary>The MIME type, e.g. <c>application/pdf</c>.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>Inline bytes, queued with the message.</summary>
    public byte[]? Content { get; init; }

    /// <summary>The RavenDB document holding the attachment; read when the mail is sent.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The RavenDB attachment's name on <see cref="DocumentId"/>.</summary>
    public string? AttachmentName { get; init; }

    /// <summary>
    /// A stored attachment that may be missing when the mail is sent: it is left out (logged) instead of failing the
    /// send. A required one that is missing fails the attempt, so the queue retries it (the file may still be in the
    /// making, e.g. a PDF rendered by another handler).
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>Inline bytes.</summary>
    public static SparkMailAttachment FromBytes(string fileName, string contentType, byte[] content)
        => new() { FileName = fileName, ContentType = contentType, Content = content };

    /// <summary>A RavenDB attachment, read when the mail is sent.</summary>
    public static SparkMailAttachment FromDocument(string documentId, string attachmentName, string fileName, string contentType = "application/pdf", bool optional = false)
        => new() { FileName = fileName, ContentType = contentType, DocumentId = documentId, AttachmentName = attachmentName, Optional = optional };
}

/// <summary>Header rules for <see cref="SparkMailRequest.Headers"/>.</summary>
public static class SparkMailHeaders
{
    /// <summary>The headers MailManager sets itself; a request cannot set them.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "From", "Sender", "To", "Cc", "Bcc", "Reply-To", "Subject", "Date", "Message-ID", "MIME-Version",
        "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Return-Path", "Received",
        "X-Spark-Template", "X-Spark-Campaign", "X-Spark-Original-To",
    };

    /// <summary>Why <paramref name="name"/>: <paramref name="value"/> is refused, or null when it is accepted.</summary>
    public static string? Problem(string? name, string? value)
    {
        if (string.IsNullOrEmpty(name))
            return "a header name is empty";
        // RFC 5322 §3.6.8: field-name = 1*ftext, ftext = %d33-57 / %d59-126 (printable ASCII except ':').
        foreach (var c in name)
            if (c < 33 || c > 126 || c == ':')
                return $"'{name}' is not a header field name";
        if (Reserved.Contains(name))
            return $"'{name}' is set by MailManager";
        if (value is null)
            return $"'{name}' has no value";
        if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            return $"the value of '{name}' contains a line break";
        return null;
    }
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
