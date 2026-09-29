using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// The moderation audit log: every lock, unlock, suspension, merge, case decision, detector reversal
/// and moderator restore / purge / revert / delete of moderatable content (§3.12, fraud measure 8).
/// </summary>
/// <remarks>Append-only documents; actors are user ids (D8), resolved to names at read time by the application.</remarks>
internal sealed partial class ModerationAudit
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task WriteAsync(
        string action,
        string? targetId,
        string? targetType,
        string? subjectUserId,
        string? caseId = null,
        string? reason = null,
        string? details = null,
        CancellationToken cancellationToken = default,
        string? actorId = null)
    {
        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(new ModerationAuditEntry
        {
            ActorId = actorId ?? (currentUser.IsAuthenticated ? currentUser.Id : null),
            Action = action,
            TargetId = targetId,
            TargetType = targetType,
            SubjectUserId = subjectUserId,
            CaseId = caseId,
            Reason = reason,
            Details = details,
            AtUtc = timeProvider.GetUtcNow().UtcDateTime,
        }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
