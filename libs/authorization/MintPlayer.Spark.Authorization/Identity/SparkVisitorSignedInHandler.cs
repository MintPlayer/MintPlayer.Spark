using MintPlayer.Spark.Abstractions.Visitor;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Merges the guest the browser was until now into the account it just signed in to (commerce PRD X1): the
/// basket, the waiting-room entry — every <see cref="ISparkVisitorMergeHandler"/> — then deletes the visitor
/// cookie.
/// </summary>
/// <remarks>
/// Runs before the ticket is issued, so <c>HttpContext.User</c> is still the guest; the account comes from
/// <paramref name="user"/>. A failure is logged by the sign-in manager and the sign-in goes ahead; Spark core's
/// merge-on-sight (<c>UseSparkVisitor</c>) then merges on the next request, and the merge runs exactly once
/// whichever path gets there first.
/// </remarks>
internal sealed class SparkVisitorSignedInHandler<TUser>(ISparkVisitorMerge? merge = null) : ISparkSignedInHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnSignedInAsync(TUser user, CancellationToken cancellationToken)
    {
        if (merge is not null && user.Id is { Length: > 0 } userId)
            await merge.MergeIntoAsync(userId, cancellationToken);
    }
}
