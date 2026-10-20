using System.Security.Claims;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Models;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>A seller registration waiting for a platform approver (D21, <c>Sellers:Registration=ApprovalRequired</c>).
/// Approving it also records the manual trader verification (DSA Art. 30(2)).</summary>
public sealed class SellerRegistrationApproval : IApprovalKind
{
    /// <inheritdoc />
    public static string Name => "commerce.seller-registration";
}

/// <summary>A best-effort manual check of a trader's data (DSA Art. 30(2)) when registration needs no approval
/// (<c>Open</c>), or when a seller changed its trader data after approval.</summary>
public sealed class TraderVerificationApproval : IApprovalKind
{
    /// <inheritdoc />
    public static string Name => "commerce.trader-verification";
}

/// <summary>A seller's complaint to the platform (P2B Reg. (EU) 2019/1150 Art. 11): a decision request with a
/// reason and a deadline (<c>Sellers:ComplaintDeadline</c>).</summary>
public sealed class SellerComplaintApproval : IApprovalKind
{
    /// <inheritdoc />
    public static string Name => "commerce.seller-complaint";
}

/// <summary>Platform admins (<see cref="CommerceRights.ManageSellers"/>) decide every commerce request. Nobody can list
/// the holders of a right, so they reach the requests through <see cref="DecidesAllAsync"/> (the identity provider's
/// go-live pattern).</summary>
internal sealed class PlatformAdminDeciders<TKind>(CommerceAccess access) : IApprovalDeciders<TKind> where TKind : IApprovalKind
{
    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context) => await access.IsPlatformAdminAsync(context.CancellationToken);

    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => await access.IsPlatformAdminAsync(cancellationToken) ? ApprovalScope.All : ApprovalScope.None;
}

/// <summary>Applies a registration decision to the seller profile, in the decision's own commit.</summary>
internal sealed class SellerRegistrationHandler(TimeProvider clock) : IApprovalHandler<SellerRegistrationApproval>
{
    public async Task ApproveAsync(ApprovalDecisionContext context)
    {
        if (await LoadAsync(context) is not { } profile) return;
        var now = clock.GetUtcNow();
        profile.Status = SellerRegistrationStatus.Approved;
        profile.ApprovedAt = now;
        profile.StatusReason = null;
        profile.RegistrationRequestId = null;
        profile.Verification = new TraderVerification { Method = "manual", Passed = true, By = context.DecidedBy, At = now, RequestId = context.Request.Id };
    }

    public async Task RejectAsync(ApprovalDecisionContext context)
    {
        if (await LoadAsync(context) is not { } profile) return;
        profile.Status = SellerRegistrationStatus.Rejected;
        profile.StatusReason = context.Reason;
        profile.RegistrationRequestId = null;
        profile.Verification = new TraderVerification { Method = "manual", Passed = false, By = context.DecidedBy, At = clock.GetUtcNow(), Notes = context.Reason, RequestId = context.Request.Id };
    }

    public async Task WithdrawAsync(ApprovalDecisionContext context)
    {
        if (await LoadAsync(context) is not { } profile) return;
        profile.Status = SellerRegistrationStatus.Draft;
        profile.RegistrationRequestId = null;
    }

    public Task ExpireAsync(ApprovalDecisionContext context) => WithdrawAsync(context);

    internal static async Task<SellerProfile?> LoadAsync(ApprovalDecisionContext context)
    {
        var profile = await context.Session.LoadAsync<SellerProfile>(context.Request.Subject.Id, context.CancellationToken);
        if (profile is null) context.MarkStale();
        return profile;
    }
}

/// <summary>Records the outcome of a manual trader check.</summary>
internal sealed class TraderVerificationHandler(TimeProvider clock) : IApprovalHandler<TraderVerificationApproval>
{
    public Task ApproveAsync(ApprovalDecisionContext context) => SetAsync(context, true);

    public Task RejectAsync(ApprovalDecisionContext context) => SetAsync(context, false);

    private async Task SetAsync(ApprovalDecisionContext context, bool passed)
    {
        if (await SellerRegistrationHandler.LoadAsync(context) is not { } profile) return;
        profile.Verification = new TraderVerification
        {
            Method = "manual",
            Passed = passed,
            By = context.DecidedBy,
            At = clock.GetUtcNow(),
            Notes = context.Reason,
            RequestId = context.Request.Id,
        };
    }
}

/// <summary>A complaint's decision is the record itself (the request keeps reason, decider and time); the seller is
/// notified by Approvals. Nothing else changes.</summary>
internal sealed class SellerComplaintHandler : IApprovalHandler<SellerComplaintApproval>
{
    public Task ApproveAsync(ApprovalDecisionContext context) => Task.CompletedTask;

    public Task RejectAsync(ApprovalDecisionContext context) => Task.CompletedTask;
}
