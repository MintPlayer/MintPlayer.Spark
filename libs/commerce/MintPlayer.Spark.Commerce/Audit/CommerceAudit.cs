using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Audit;

/// <summary>
/// The money audit (default 8): an append-only <see cref="CommerceAuditRecord"/> stored in the caller's session, so the
/// change and its audit entry commit together or not at all.
/// </summary>
internal sealed class CommerceAudit(TimeProvider clock, ISparkCurrentUser? currentUser = null) : ICommerceAudit
{
    internal const string Collection = "CommerceAudit";

    public async Task RecordAsync(IAsyncDocumentSession session, CommerceAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SubjectId);

        var at = entry.At ?? clock.GetUtcNow();
        var record = new CommerceAuditRecord
        {
            // Unique per subject and instant; the Guid suffix keeps two entries of one instant apart.
            Id = $"{Collection}/{entry.SubjectId}/{at.UtcTicks:D19}-{Guid.NewGuid():N}",
            Action = entry.Action,
            SubjectId = entry.SubjectId,
            OrganizationId = entry.OrganizationId,
            Before = entry.Before,
            After = entry.After,
            Reason = entry.Reason,
            ActorId = entry.ActorId ?? currentUser?.Id,
            At = at,
            Data = entry.Data?.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        await session.StoreAsync(record, record.Id, cancellationToken);
    }
}
