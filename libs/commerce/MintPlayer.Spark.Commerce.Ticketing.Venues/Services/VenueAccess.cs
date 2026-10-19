using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Services;

/// <summary>
/// Who may do what with layouts (D8): the owner edits the draft and publishes; the holders of
/// <c>ApproveProposal/VenueLayout</c> (platform approvers) read every plan and publish without a request; every
/// signed-in user reads published versions and copies them.
/// </summary>
public sealed class VenueAccess(ISparkCurrentUser currentUser, IPermissionService permissions)
{
    public const string VenueType = "Venue";
    public const string LayoutType = "VenueLayout";

    private bool? approver;

    public string? CallerId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    public async Task<bool> IsApproverAsync(CancellationToken cancellationToken = default)
        => approver ??= CallerId is not null && await permissions.IsAllowedAsync(ApprovalRights.ApproveProposal, LayoutType, cancellationToken);

    public bool Owns(VenueLayout layout) => CallerId is { } me && string.Equals(layout.OwnerId, me, StringComparison.Ordinal);

    /// <summary>The draft and a pending proposal: the owner and the approvers.</summary>
    public async Task<bool> MayReadPrivateAsync(VenueLayout layout, CancellationToken cancellationToken = default)
        => Owns(layout) || await IsApproverAsync(cancellationToken);

    /// <summary>A published version: every signed-in user (published layouts are a shared catalogue, D8).</summary>
    public bool MayReadPublished(VenueLayout layout) => CallerId is not null && layout.PublishedVersion > 0;
}
