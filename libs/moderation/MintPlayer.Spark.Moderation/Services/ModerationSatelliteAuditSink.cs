using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// Moderation's audit of moderator actions on satellite documents (contributions M5b: a removed
/// version, a revert): one append-only <c>ModerationAuditEntry</c> per action, actor = the caller.
/// <c>Details</c> lists every document the action hid or deleted.
/// </summary>
/// <remarks>
/// <c>TargetType</c> is the satellite type's id, like every other moderation record (D28); it used to be
/// the CLR simple name, inconsistent with the rest of the log. A satellite class the model does not know
/// has no id: its CLR full name stays readable in <c>Details</c>.
/// </remarks>
internal sealed class ModerationSatelliteAuditSink(ModerationAudit audit, IModelLoader modelLoader) : ISatelliteAuditSink
{
    public async ValueTask RecordAsync(SatelliteAuditEntry entry, CancellationToken cancellationToken = default)
        => await audit.WriteAsync(
            entry.Action,
            targetId: entry.DocumentId,
            targetType: modelLoader.GetEntityTypeByClrType(entry.DocumentType.FullName!) is { } definition ? ModerationTypeIds.Of(definition) : null,
            subjectUserId: null,
            reason: entry.Reason,
            details: $"{entry.DocumentType.FullName} {entry.DocumentId} of {entry.TargetType.FullName} {entry.TargetId}: {string.Join(", ", entry.AffectedDocumentIds)}",
            cancellationToken: cancellationToken);
}
