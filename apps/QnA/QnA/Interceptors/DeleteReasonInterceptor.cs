using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;
using QnA.Entities;

namespace QnA.Interceptors;

/// <summary>
/// Removing someone else's question or answer asks why (#467, D20): the reason is recorded on the
/// soft-deleted post (<see cref="ISoftDeletable.DeleteReason"/>) and handed to the durable after-commit
/// interceptors as the <see cref="SparkFacts.Reason"/> fact.
/// <para>
/// The framework records a reason the request carries; asking for one is the application's choice, so
/// it is an ordinary retry action here, the same shape as Fleet's plate confirmation. Authors removing
/// their own post are not asked. A retry cannot be answered per row inside a bulk delete, so a bulk delete
/// of other people's posts is refused unless the request carries a reason (#482, D33).
/// </para>
/// </summary>
public sealed partial class DeleteReasonInterceptor : IBeforeDelete
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly Services.QnAAccess access;

    public bool AppliesTo(Type entityType) => entityType == typeof(Question) || entityType == typeof(Answer);

    public async ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        // Only a soft delete is recorded with a reason; a purge removes the row and its reason with it.
        if (!context.IsReplaced || context.IsSystemContext
            || context.Entity is not ISoftDeletable deletable || context.Entity is not IModeratable post)
            return;

        if (!string.IsNullOrWhiteSpace(deletable.DeleteReason) || post.AuthorId == access.UserId)
            return;

        manager.Retry.Action(
            title: "Remove this post",
            options: ["Delete"],
            cancellable: true,
            persistentObject: await manager.GetPersistentObjectAsync(Guid.Parse(PersistentObjectIds.Default.DeleteReason)),
            message: "Why is it being removed? The reason is kept with the post.");

        var result = manager.Retry.Result!;
        if (result.Option == "Cancel")
            throw new SparkCancelException();

        var reason = result.PersistentObject?["Reason"].Value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw new SparkValidationException("Give a reason for removing this post.");

        deletable.DeleteReason = reason;
        context.Facts[SparkFacts.Reason] = reason;
    }
}
