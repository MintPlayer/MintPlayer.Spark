using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Services;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce.Shop.Services;

/// <summary>
/// Who may do what in the shop: a seller's members (Member and up, X8) ship and inspect that seller's orders; platform
/// admins (<c>ManageSellers/Commerce</c>) every seller's and the platform's own; Moderation's reviewers
/// (<c>Review/Moderation</c>) decide pending reviews.
/// </summary>
internal sealed class ShopAccess(ISparkCurrentUser currentUser, ISparkOrganizations organizations, IAccessControl accessControl)
{
    public string? UserId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    public async Task<bool> MayHandleOrdersAsync(string? organizationId, CancellationToken cancellationToken = default)
    {
        if (UserId is not { } userId)
            return false;
        if (organizationId is not null && await organizations.GetRoleAsync(organizationId, userId, cancellationToken) is { } role && role >= OrganizationRole.Member)
            return true;
        return await accessControl.IsAllowedAsync(CommerceRights.ManageSellers, cancellationToken);
    }

    public Task<bool> MayDecideReviewsAsync(CancellationToken cancellationToken = default)
        => UserId is null ? Task.FromResult(false) : accessControl.IsAllowedAsync($"{ModerationRights.Review}/{ModerationRights.Target}", cancellationToken);
}
