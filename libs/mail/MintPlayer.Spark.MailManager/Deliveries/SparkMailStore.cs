using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;

namespace MintPlayer.Spark.MailManager.Deliveries;

/// <summary>What happened to one delivery.</summary>
public enum SparkMailDeliveryStatus
{
    /// <summary>Handed to the transport.</summary>
    Sent,
    /// <summary>Not sent: the recipient is suppressed (the message still completes).</summary>
    Suppressed,
    /// <summary>A permanent failure at send time (5xx, template error).</summary>
    Failed,
    /// <summary>A bounce report arrived for it after sending.</summary>
    Bounced,
    /// <summary>A complaint (an RFC 5965 feedback report) arrived for it; <see cref="SparkMailDelivery.Detail"/> names the feedback type.</summary>
    Complained,
}

/// <summary>
/// <c>sparkmaildeliveries/{deliveryId}</c>: what a VERP bounce is matched against. Kept for
/// <see cref="SparkMailOptions.DeliveryRetentionDays"/> (RavenDB <c>@expires</c>). Holds the address —
/// personal data, which is why it expires.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class SparkMailDelivery
{
    /// <summary>The document id.</summary>
    public Id<SparkMailDelivery> Id { get; set; }

    /// <summary>The recipient.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The template.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The stream.</summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>The campaign, if any.</summary>
    public string? CampaignId { get; set; }

    /// <summary>The status.</summary>
    public SparkMailDeliveryStatus Status { get; set; }

    /// <summary>When the status was last set.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The failure or bounce reason (SMTP reply, DSN status).</summary>
    public string? Detail { get; set; }

    /// <summary>The document id for <paramref name="deliveryId"/>.</summary>
    public static Id<SparkMailDelivery> IdFor(string deliveryId) => Id<SparkMailDelivery>.FromTrusted($"sparkmaildeliveries/{deliveryId}");
}

/// <summary>
/// <c>sparkmailsuppressions/{sha256(normalized email)}</c>: the address is not stored, only its hash.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class SparkMailSuppression
{
    /// <summary>The document id.</summary>
    public Id<SparkMailSuppression> Id { get; set; }

    /// <summary>Suppressed for every stream.</summary>
    public bool All { get; set; }

    /// <summary>The streams suppressed individually (unsubscribes).</summary>
    public List<string> Streams { get; set; } = [];

    /// <summary>The last reason recorded.</summary>
    public SparkMailSuppressionReason Reason { get; set; }

    /// <summary>When it was last changed.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The document id for <paramref name="email"/>.</summary>
    public static Id<SparkMailSuppression> IdFor(string email)
        => Id<SparkMailSuppression>.FromTrusted($"sparkmailsuppressions/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(email))))}");

    /// <summary>Trimmed and lower-cased (the local part is case-insensitive in practice).</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}

/// <inheritdoc />
internal sealed class SparkMailSuppressions(IDocumentStore store) : ISparkMailSuppressions
{
    public async Task<bool> IsSuppressedAsync(string email, string? stream = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var doc = await session.LoadAsync<SparkMailSuppression>(SparkMailSuppression.IdFor(email).Value, cancellationToken);
        return doc is not null && (doc.All || (stream is not null && doc.Streams.Contains(stream, StringComparer.OrdinalIgnoreCase)));
    }

    public async Task SuppressAsync(string email, SparkMailSuppressionReason reason, string? stream = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var id = SparkMailSuppression.IdFor(email);
        var doc = await session.LoadAsync<SparkMailSuppression>(id.Value, cancellationToken);
        if (doc is null)
        {
            doc = new SparkMailSuppression { Id = id };
            await session.StoreAsync(doc, id.Value, cancellationToken);
        }
        if (stream is null)
            doc.All = true;
        else if (!doc.Streams.Contains(stream, StringComparer.OrdinalIgnoreCase))
            doc.Streams.Add(stream);
        doc.Reason = reason;
        doc.UpdatedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(string email, string? stream = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var doc = await session.LoadAsync<SparkMailSuppression>(SparkMailSuppression.IdFor(email).Value, cancellationToken);
        if (doc is null)
            return false;
        if (stream is null)
            session.Delete(doc);
        else
        {
            if (doc.Streams.RemoveAll(s => string.Equals(s, stream, StringComparison.OrdinalIgnoreCase)) == 0)
                return false;
            if (!doc.All && doc.Streams.Count == 0)
                session.Delete(doc);
        }
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Writes <see cref="SparkMailDelivery"/> records.</summary>
internal sealed class SparkMailDeliveryStore(IDocumentStore store, IOptions<SparkMailOptions> options)
{
    public async Task RecordAsync(string deliveryId, Action<SparkMailDelivery> update, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var id = SparkMailDelivery.IdFor(deliveryId);
        var doc = await session.LoadAsync<SparkMailDelivery>(id.Value, cancellationToken);
        if (doc is null)
        {
            doc = new SparkMailDelivery { Id = id };
            await session.StoreAsync(doc, id.Value, cancellationToken);
            session.Advanced.GetMetadataFor(doc)["@expires"] = DateTime.UtcNow.AddDays(options.Value.DeliveryRetentionDays).ToString("O");
        }
        update(doc);
        doc.UpdatedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task<SparkMailDelivery?> LoadAsync(string deliveryId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<SparkMailDelivery>(SparkMailDelivery.IdFor(deliveryId).Value, cancellationToken);
    }
}
