using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Approvals.CustomActions;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Approvals.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Approvals.Actions;

/// <summary>
/// The requests a caller may see (<see cref="ApprovalVisibility.RowFilterAsync"/>) on every read path, the live socket
/// included: another user's request is the missing-row 404 (#453), and an anonymous caller has none. The decision
/// actions are offered only where they would succeed: Approve and Reject to a decider of a pending request, Withdraw
/// to its requester, Revoke to a decider of an approved one.
/// </summary>
public partial class ApprovalRequestActions : DefaultPersistentObjectActions<ApprovalRequest>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ApprovalVisibility visibility;
    [Inject] private readonly SparkApprovals approvals;

    public override async Task<Expression<Func<ApprovalRequest, bool>>?> GetRowFilterAsync(string action)
        => await visibility.RowFilterAsync();

    /// <summary>Every request the caller may see, newest first by the query's sort.</summary>
    public IRavenQueryable<ApprovalRequest> Approvals() => session.Query<ApprovalRequest>();

    /// <summary>The inbox: pending requests someone else raised. Its <c>totalItems</c> is the badge count.</summary>
    public IQueryable<ApprovalRequest> PendingApprovals()
    {
        var me = visibility.CallerId ?? string.Empty;
        return session.Query<ApprovalRequest>().Where(r => r.Status == ApprovalStatuses.Pending && r.RequestedBy != me);
    }

    /// <summary>The requests the caller raised.</summary>
    public IQueryable<ApprovalRequest> MyApprovalRequests()
    {
        var me = visibility.CallerId ?? string.Empty;
        return session.Query<ApprovalRequest>().Where(r => r.RequestedBy == me);
    }

    public override async Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is not ApprovalRequest request)
            return;

        var open = approvals.IsOpen(request);
        var mayDecide = await visibility.MayDecideAsync(request, session);
        if (!open || !mayDecide)
            target.DisableActions(ApprovalActionNames.Approve, ApprovalActionNames.Reject);
        if (!open || !visibility.MayWithdraw(request))
            target.DisableActions(ApprovalActionNames.Withdraw);
        if (request.Status != ApprovalStatuses.Approved || request.Proposal is not null || !mayDecide)
            target.DisableActions(ApprovalActionNames.Revoke);
    }
}
