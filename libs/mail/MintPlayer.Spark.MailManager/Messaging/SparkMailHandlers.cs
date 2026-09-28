using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MintPlayer.Spark.MailManager.Bounces;
using MintPlayer.Spark.MailManager.Deliveries;
using MintPlayer.Spark.MailManager.Templates;
using MintPlayer.Spark.MailManager.Transports;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.MailManager.Messaging;

/// <summary>Renders and sends one <see cref="SparkMailMessage"/>.</summary>
internal sealed class SparkMailHandler(
    SparkMailRenderer renderer,
    ISparkMailTransport transport,
    ISparkMailSuppressions suppressions,
    SparkMailDeliveryStore deliveries,
    SparkMailPayloadProtector protector,
    SparkMailUnsubscribeTokens unsubscribeTokens,
    IOptions<SparkMailOptions> options,
    IHostEnvironment environment,
    ILogger<SparkMailHandler> logger) : IRecipient<SparkMailMessage>
{
    public async Task HandleAsync(SparkMailMessage message, CancellationToken cancellationToken = default)
    {
        // Checked again at send: a bounce or unsubscribe may have arrived while the mail was queued.
        if (await suppressions.IsSuppressedAsync(message.To, message.Stream, cancellationToken))
        {
            await deliveries.RecordAsync(message.DeliveryId, d => Fill(d, message, SparkMailDeliveryStatus.Suppressed, null), cancellationToken);
            logger.LogInformation("Mail {DeliveryId} ({Template}) not sent: the recipient is suppressed.", message.DeliveryId, message.Template);
            return;
        }

        MimeMessage mime;
        try
        {
            mime = await BuildAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is SparkMailTemplateException or CryptographicException)
        {
            // A retry renders the same template with the same data: it cannot succeed.
            logger.LogError(ex, "Mail {DeliveryId} ({Template}) cannot be rendered; not retried.", message.DeliveryId, message.Template);
            await deliveries.RecordAsync(message.DeliveryId, d => Fill(d, message, SparkMailDeliveryStatus.Failed, ex.Message), cancellationToken);
            throw new NonRetryableException($"Mail template '{message.Template}' cannot be rendered: {ex.Message}", ex);
        }

        var o = options.Value;
        var recipient = new MailboxAddress(message.ToName ?? string.Empty, message.To);
        if (!string.IsNullOrWhiteSpace(o.Development.RedirectTo))
        {
            if (!environment.IsDevelopment())
                logger.LogWarning("Spark:Mail:Development:RedirectTo is set outside Development: mail {DeliveryId} goes to the redirect address, not its recipient.", message.DeliveryId);
            mime.Headers.Replace("X-Spark-Original-To", message.To);
            recipient = MailboxAddress.Parse(o.Development.RedirectTo);
            mime.To.Clear();
            mime.To.Add(recipient);
        }

        var envelopeFrom = string.IsNullOrWhiteSpace(o.Bounces.VerpDomain)
            ? new MailboxAddress(string.Empty, o.From.Address!)
            : new MailboxAddress(string.Empty, SparkMailVerp.Address(message.DeliveryId, o.Bounces.VerpDomain));

        try
        {
            await transport.SendAsync(mime, envelopeFrom, recipient, cancellationToken);
        }
        catch (NonRetryableException ex)
        {
            await deliveries.RecordAsync(message.DeliveryId, d => Fill(d, message, SparkMailDeliveryStatus.Failed, ex.Message), cancellationToken);
            throw;
        }
        await deliveries.RecordAsync(message.DeliveryId, d => Fill(d, message, SparkMailDeliveryStatus.Sent, null), cancellationToken);
    }

    private static void Fill(SparkMailDelivery delivery, SparkMailMessage message, SparkMailDeliveryStatus status, string? detail)
    {
        delivery.Email = message.To;
        delivery.Template = message.Template;
        delivery.Stream = message.Stream;
        delivery.CampaignId = message.CampaignId;
        delivery.Status = status;
        delivery.Detail = detail;
    }

    internal async Task<MimeMessage> BuildAsync(SparkMailMessage message, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var culture = string.IsNullOrEmpty(message.Culture) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(message.Culture);
        var json = protector.Unprotect(message.Data);
        var data = SparkMailRenderer.WithAppName(json, o.ApplicationName);
        var rendered = await renderer.RenderAsync(message.Template, culture, data, cancellationToken);
        foreach (var warning in rendered.Warnings)
            logger.LogWarning("Mail template {Template}: MJML {Warning}", rendered.Template.Path, warning);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.From.Name ?? string.Empty, o.From.Address!));
        mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.To));
        mime.Subject = rendered.Subject;
        // <deliveryId@domain>: an at-least-once duplicate carries the same id, so it is identifiable.
        mime.MessageId = $"{message.DeliveryId}@{MessageIdDomain(o)}";
        mime.Headers.Replace("X-Spark-Template", message.Template);
        if (!string.IsNullOrEmpty(message.CampaignId))
            mime.Headers.Replace("X-Spark-Campaign", message.CampaignId);

        if (message.ListUnsubscribe)
        {
            if (unsubscribeTokens.Link(message.To, message.Stream) is { } link)
            {
                // RFC 8058 one-click: the receiver POSTs "List-Unsubscribe=One-Click" to the URL.
                mime.Headers.Replace(HeaderId.ListUnsubscribe, $"<{link}>");
                mime.Headers.Replace("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
            }
            else
                logger.LogWarning("Mail {DeliveryId}: List-Unsubscribe requested but no public base URL is configured (Spark:Mail:PublicBaseUrl or Spark:Auth:PublicBaseUrl); header left out.", message.DeliveryId);
        }

        var body = new BodyBuilder { HtmlBody = rendered.Html, TextBody = rendered.Text };
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static string MessageIdDomain(SparkMailOptions o)
    {
        var from = o.From.Address!;
        var at = from.LastIndexOf('@');
        return at >= 0 ? from[(at + 1)..] : "spark.local";
    }
}

/// <summary>Fans a campaign out into one bulk mail per recipient.</summary>
internal sealed class SparkMailCampaignHandler(
    IMessageBus bus,
    SparkMailer mailer,
    ILogger<SparkMailCampaignHandler> logger) : IRecipient<SparkMailCampaignMessage>
{
    public async Task HandleAsync(SparkMailCampaignMessage message, CancellationToken cancellationToken = default)
    {
        var shared = message.Data is null ? null : JsonNode.Parse(message.Data) as JsonObject;
        var queued = 0;
        foreach (var recipient in message.Recipients)
        {
            var data = Merge(shared, recipient.Data);
            var mail = new SparkMailMessage
            {
                DeliveryId = SparkMailer.CampaignDeliveryId(message.CampaignId, recipient.Email),
                Template = message.Template,
                To = recipient.Email,
                ToName = recipient.Name,
                Culture = await mailer.ResolveCultureAsync(recipient.Culture, recipient.Email, cancellationToken),
                Data = data?.ToJsonString(),
                Stream = message.Stream,
                ListUnsubscribe = message.ListUnsubscribe,
                CampaignId = message.CampaignId,
            };
            // One message per recipient, deduplicated on "{campaignId}:{recipient}", so a retried
            // fan-out (a crash half way) never mails anyone twice — and never one mail with many BCCs.
            await bus.BroadcastAsync(mail, new BroadcastOptions
            {
                Queue = SparkMailQueues.Bulk,
                DeduplicationKey = $"{message.CampaignId}:{recipient.Email.Trim().ToLowerInvariant()}",
                ExpiresAtUtc = message.ExpiresAtUtc,
            }, cancellationToken);
            queued++;
        }
        logger.LogInformation("Campaign {CampaignId}: {Count} mails queued.", message.CampaignId, queued);
    }

    private static JsonObject? Merge(JsonObject? shared, string? own)
    {
        var mine = own is null ? null : JsonNode.Parse(own) as JsonObject;
        if (shared is null) return mine;
        var result = (JsonObject)shared.DeepClone();
        if (mine is not null)
            foreach (var (key, value) in mine)
                result[key] = value?.DeepClone();
        return result;
    }
}

/// <summary>VERP addresses: <c>bounces+{deliveryId}@{domain}</c>.</summary>
public static class SparkMailVerp
{
    /// <summary>The envelope sender for <paramref name="deliveryId"/>.</summary>
    public static string Address(string deliveryId, string domain) => $"bounces+{deliveryId}@{domain}";

    /// <summary>The delivery id in a VERP address, or null (only 32 hex digits are accepted).</summary>
    public static string? DeliveryId(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        var start = address.IndexOf("bounces+", StringComparison.OrdinalIgnoreCase);
        var at = address.IndexOf('@', Math.Max(0, start));
        if (start < 0 || at < 0) return null;
        var id = address[(start + "bounces+".Length)..at];
        return id.Length == 32 && id.All(Uri.IsHexDigit) ? id.ToLowerInvariant() : null;
    }
}
