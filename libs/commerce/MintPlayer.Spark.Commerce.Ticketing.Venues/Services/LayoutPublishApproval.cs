using System.Globalization;
using System.Security.Claims;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Services;

/// <summary>
/// A layout publication waiting for a platform approver (D8): every new version of a published layout, and the first
/// version when <c>ApproveCreations</c> is on. The subject is the layout; the frozen proposal plan is what is approved.
/// </summary>
public sealed class LayoutPublishApproval : IApprovalKind
{
    /// <inheritdoc />
    public static string Name => "ticketing-venues.layout-publish";
}

/// <summary>
/// The approvers of layouts are the holders of <c>ApproveProposal/VenueLayout</c>, the same right that decides
/// proposals of the layout's own attributes (the app grants it, Moderation style; no library slot). Nobody can list a
/// right's holders, so they reach the requests through <see cref="DecidesAllAsync"/>.
/// </summary>
internal sealed class LayoutApprovers(IPermissionService permissions) : IApprovalDeciders<LayoutPublishApproval>
{
    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context)
        => await permissions.IsAllowedAsync(ApprovalRights.ApproveProposal, VenueAccess.LayoutType, context.CancellationToken);

    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => await permissions.IsAllowedAsync(ApprovalRights.ApproveProposal, VenueAccess.LayoutType, cancellationToken)
            ? ApprovalScope.Types([VenueAccess.LayoutType])
            : ApprovalScope.None;
}

/// <summary>
/// Applies a publication decision in the decision's own commit: approve copies the frozen proposal to the next version
/// (stale when another version was published meanwhile); reject, withdraw and expire return the layout to its
/// previous state and drop the proposal.
/// </summary>
internal sealed class LayoutPublishHandler(TimeProvider clock) : IApprovalHandler<LayoutPublishApproval>
{
    internal const string BaseVersionKey = "baseVersion";

    public async Task ApproveAsync(ApprovalDecisionContext context)
    {
        var session = context.Session;
        var layout = await session.LoadAsync<VenueLayout>(context.Request.Subject.Id, context.CancellationToken);
        var proposal = layout is null ? null : await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.ProposalId(layout.Id!), context.CancellationToken);
        var baseVersion = context.Request.Data.TryGetValue(BaseVersionKey, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
        if (layout is null || proposal is null || layout.PublishedVersion != baseVersion || layout.PendingRequestId != context.Request.Id)
        {
            context.MarkStale();
            return;
        }
        await SparkVenues.PublishVersionAsync(session, layout, proposal.Plan, proposal.Revision, clock.GetUtcNow().UtcDateTime, context.CancellationToken);
        session.Delete(proposal.Id!);
    }

    public Task RejectAsync(ApprovalDecisionContext context) => CloseAsync(context, context.Reason);

    public Task WithdrawAsync(ApprovalDecisionContext context) => CloseAsync(context, null);

    public Task ExpireAsync(ApprovalDecisionContext context) => CloseAsync(context, null);

    private static async Task CloseAsync(ApprovalDecisionContext context, string? reason)
    {
        var layout = await context.Session.LoadAsync<VenueLayout>(context.Request.Subject.Id, context.CancellationToken);
        if (layout is null || layout.PendingRequestId != context.Request.Id)
            return;
        layout.PendingRequestId = null;
        layout.State = layout.PublishedVersion > 0 ? LayoutState.Published : LayoutState.Draft;
        layout.LastRejection = reason;
        context.Session.Delete(VenueLayoutPlan.ProposalId(layout.Id!));
    }
}
