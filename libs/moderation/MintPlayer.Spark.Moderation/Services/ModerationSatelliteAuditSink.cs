using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// Moderation's audit of moderator actions on satellite documents (contributions M5b: a removed
/// version, a revert): one append-only <c>ModerationAuditEntry</c> per action, actor = the caller.
/// <c>Details</c> lists every document the action hid or deleted.
/// </summary>
internal sealed class ModerationSatelliteAuditSink(ModerationAudit audit) : ISatelliteAuditSink
{
    public async ValueTask RecordAsync(SatelliteAuditEntry entry, CancellationToken cancellationToken = default)
        => await audit.WriteAsync(
            entry.Action,
            targetId: entry.DocumentId,
            targetType: entry.DocumentType.Name,
            subjectUserId: null,
            reason: entry.Reason,
            details: $"{entry.TargetType.Name} {entry.TargetId}: {string.Join(", ", entry.AffectedDocumentIds)}",
            cancellationToken: cancellationToken);
}
