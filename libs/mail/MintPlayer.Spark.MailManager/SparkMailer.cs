using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.MailManager.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.MailManager;

/// <summary>Data Protection for queued template data (#460, T4).</summary>
internal sealed class SparkMailPayloadProtector(IDataProtectionProvider provider)
{
    private const string Prefix = "sdp1:";
    private readonly IDataProtector protector = provider.CreateProtector("MintPlayer.Spark.MailManager.Payload.v1");

    public string? Protect(string? json, bool sensitive) => json is null || !sensitive ? json : Prefix + protector.Protect(json);

    public string? Unprotect(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal)
        ? protector.Unprotect(stored[Prefix.Length..])
        : stored;
}

/// <inheritdoc />
internal sealed class SparkMailer(
    IMessageBus bus,
    ISparkMailSuppressions suppressions,
    IEnumerable<ISparkMailRecipientCulture> recipientCultures,
    SparkMailPayloadProtector protector,
    IOptions<SparkMailOptions> options,
    ILogger<SparkMailer> logger) : ISparkMailer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SparkMailReceipt> SendAsync(SparkMailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Template);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.To);

        var deliveryId = NewDeliveryId();
        var stream = request.Stream ?? request.Template;
        if (await suppressions.IsSuppressedAsync(request.To, stream, cancellationToken))
        {
            logger.LogInformation("Mail {Template} not queued: the recipient is suppressed for {Stream}.", request.Template, stream);
            return new SparkMailReceipt(deliveryId, Suppressed: true);
        }

        var queue = request.Queue ?? SparkMailQueues.Transactional;
        var message = new SparkMailMessage
        {
            DeliveryId = deliveryId,
            Template = request.Template,
            To = request.To.Trim(),
            ToName = request.ToName,
            Culture = await ResolveCultureAsync(request.Culture, request.To, cancellationToken),
            Data = protector.Protect(SerializeData(request.Data), request.Sensitive),
            Stream = stream,
            ListUnsubscribe = request.ListUnsubscribe ?? queue == SparkMailQueues.Bulk,
            Queue = queue,
            Attachments = MapAttachments(request.Attachments, request.Sensitive),
            Headers = MapHeaders(request.Headers),
        };

        await bus.BroadcastAsync(message, new BroadcastOptions
        {
            Queue = queue,
            Delay = request.Delay,
            DeduplicationKey = request.DeduplicationKey,
            ExpiresAtUtc = request.ExpiresAtUtc,
            ScrubPayloadOnTerminal = request.Sensitive,
        }, cancellationToken);
        return new SparkMailReceipt(deliveryId, Suppressed: false);
    }

    public async Task SendCampaignAsync(SparkMailCampaign campaign, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaign.CampaignId);
        ArgumentException.ThrowIfNullOrWhiteSpace(campaign.Template);

        var message = new SparkMailCampaignMessage
        {
            CampaignId = campaign.CampaignId,
            Template = campaign.Template,
            Stream = campaign.Stream ?? campaign.Template,
            Data = SerializeData(campaign.Data),
            ListUnsubscribe = campaign.ListUnsubscribe,
            ExpiresAtUtc = campaign.ExpiresAtUtc,
            Recipients = [.. campaign.Recipients.Select(r => new SparkMailCampaignMessageRecipient
            {
                Email = r.Email.Trim(),
                Name = r.Name,
                Culture = r.Culture,
                Data = SerializeData(r.Data),
            })],
        };

        // The recipient list is personal data: scrubbed once the fan-out is done.
        await bus.BroadcastAsync(message, new BroadcastOptions
        {
            DeduplicationKey = $"campaign:{campaign.CampaignId}",
            ExpiresAtUtc = campaign.ExpiresAtUtc,
            ScrubPayloadOnTerminal = true,
        }, cancellationToken);
    }

    /// <summary>
    /// Explicit → the recipient's stored preference (first provider with an answer) →
    /// <c>Spark:Mail:DefaultCulture</c>. Never <see cref="CultureInfo.CurrentUICulture"/>: mail is
    /// rendered on a worker, where the ambient culture belongs to nobody.
    /// </summary>
    internal async Task<string> ResolveCultureAsync(string? explicitCulture, string email, CancellationToken cancellationToken)
    {
        if (Normalize(explicitCulture) is { } chosen)
            return chosen;
        foreach (var provider in recipientCultures)
            if (Normalize(await provider.GetCultureAsync(email, cancellationToken)) is { } preferred)
                return preferred;
        return Normalize(options.Value.DefaultCulture) ?? string.Empty;
    }

    private string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
            return null;
        try
        {
            return CultureInfo.GetCultureInfo(culture.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            logger.LogWarning("Ignoring unknown mail culture '{Culture}'.", culture);
            return null;
        }
    }

    /// <summary>Validates and maps the attachments; refuses a bad one at queue time, where the caller can still react.</summary>
    internal List<SparkMailMessageAttachment>? MapAttachments(IReadOnlyList<SparkMailAttachment>? attachments, bool sensitive)
    {
        if (attachments is not { Count: > 0 })
            return null;
        var result = new List<SparkMailMessageAttachment>(attachments.Count);
        long inline = 0;
        foreach (var attachment in attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            if (AttachmentProblem(attachment) is { } problem)
                throw new ArgumentException($"Mail attachment refused: {problem}.", nameof(attachments));
            if (attachment.Content is { } bytes)
            {
                inline += bytes.LongLength;
                if (inline > options.Value.Attachments.MaxInlineBytes)
                    throw new ArgumentException($"Mail attachments refused: more than {options.Value.Attachments.MaxInlineBytes} inline bytes (Spark:Mail:Attachments:MaxInlineBytes); store large files as RavenDB attachments and use SparkMailAttachment.FromDocument.", nameof(attachments));
            }
            result.Add(new SparkMailMessageAttachment
            {
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                Content = attachment.Content is { } content ? protector.Protect(Convert.ToBase64String(content), sensitive) : null,
                DocumentId = attachment.DocumentId,
                AttachmentName = attachment.AttachmentName,
                Optional = attachment.Optional,
            });
        }
        return result;
    }

    internal static string? AttachmentProblem(SparkMailAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.FileName))
            return "no file name";
        if (attachment.FileName.AsSpan().IndexOfAny("\r\n\0/\\\"") >= 0 || attachment.FileName.Length > 200)
            return $"'{attachment.FileName}' is not a plain file name";
        if (!MimeKit.ContentType.TryParse(attachment.ContentType ?? string.Empty, out _))
            return $"'{attachment.ContentType}' is not a MIME type";
        var stored = attachment.DocumentId is not null || attachment.AttachmentName is not null;
        if (attachment.Content is not null == stored)
            return $"'{attachment.FileName}' needs exactly one source: Content, or DocumentId + AttachmentName";
        if (stored && (string.IsNullOrWhiteSpace(attachment.DocumentId) || string.IsNullOrWhiteSpace(attachment.AttachmentName)))
            return $"'{attachment.FileName}' names a stored attachment without both DocumentId and AttachmentName";
        return null;
    }

    internal static Dictionary<string, string>? MapHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not { Count: > 0 })
            return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (SparkMailHeaders.Problem(name, value) is { } problem)
                throw new ArgumentException($"Mail header refused: {problem}.", nameof(headers));
            result[name] = value;
        }
        return result;
    }

    internal static string? SerializeData(object? data) => data switch
    {
        null => null,
        string s => s,
        JsonElement e => e.GetRawText(),
        JsonNode n => n.ToJsonString(),
        _ => JsonSerializer.Serialize(data, data.GetType(), Json),
    };

    /// <summary>A compact random id: 32 lower-case hex digits.</summary>
    internal static string NewDeliveryId() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// A campaign recipient's delivery id: UUIDv5 (RFC 9562, SHA-1) of <c>{campaignId}\n{normalized email}</c>,
    /// so a re-run of the fan-out names the same delivery.
    /// </summary>
    internal static string CampaignDeliveryId(string campaignId, string email)
    {
        Span<byte> ns = stackalloc byte[16];
        CampaignNamespace.TryWriteBytes(ns, bigEndian: true, out _);
        var name = Encoding.UTF8.GetBytes($"{campaignId}\n{email.Trim().ToLowerInvariant()}");
        var buffer = new byte[16 + name.Length];
        ns.CopyTo(buffer);
        name.CopyTo(buffer, 16);
        var hash = SHA1.HashData(buffer);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexStringLower(hash, 0, 16);
    }

    private static readonly Guid CampaignNamespace = Guid.Parse("5f0b8b4e-7c1d-4f7e-9a51-3a4d2c0e8b17");
}
