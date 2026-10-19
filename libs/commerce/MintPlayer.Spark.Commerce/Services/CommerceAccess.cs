using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>
/// The rights Commerce ships (App_Data/security.json, on the reserved <c>Commerce</c> pseudo-type) and the custom
/// organization roles it declares (X8).
/// </summary>
public static class CommerceRights
{
    /// <summary>Platform admins: decide seller registrations, trader checks and complaints; publish platform legal
    /// documents; suspend sellers.</summary>
    public const string ManageSellers = "ManageSellers/Commerce";

    /// <summary>Download a seller's identity document and read its full trader and DAC7 data (audited).</summary>
    public const string ViewTraderData = "ViewTraderData/Commerce";

    /// <summary>Decide DSA notices and issue statements of reasons; export the transparency report.</summary>
    public const string DecideNotices = "DecideNotices/Commerce";

    /// <summary>The organization custom role that sees money and may export buyers (X8).</summary>
    public const string FinanceRole = "finance";
}

/// <summary>Who may do what in Commerce: platform rights through <see cref="IAccessControl"/>, seller staff through
/// their organization role.</summary>
internal sealed class CommerceAccess(
    IAccessControl accessControl,
    ISparkOrganizations organizations,
    IOptions<SparkCommerceOptions> options,
    ISparkCurrentUser currentUser)
{
    public string? UserId => currentUser.Id;

    public Task<bool> IsPlatformAdminAsync(CancellationToken ct = default) => accessControl.IsAllowedAsync(CommerceRights.ManageSellers, ct);

    public Task<bool> MayViewTraderDataAsync(CancellationToken ct = default) => accessControl.IsAllowedAsync(CommerceRights.ViewTraderData, ct);

    public Task<bool> MayDecideNoticesAsync(CancellationToken ct = default) => accessControl.IsAllowedAsync(CommerceRights.DecideNotices, ct);

    /// <summary>Organization Admin or Owner: edits the seller profile, publishes the seller's legal documents.</summary>
    public async Task<bool> MayManageSellerAsync(string organizationId, CancellationToken ct = default)
    {
        if (currentUser.Id is not { } userId) return false;
        return await organizations.GetRoleAsync(organizationId, userId, ct) is OrganizationRole.Admin or OrganizationRole.Owner
            || await IsPlatformAdminAsync(ct);
    }

    /// <summary>Exports of buyers and attendees (GB14): Admin/Owner, or the <c>finance</c> or export custom role.</summary>
    public async Task<bool> MayExportAsync(string organizationId, CancellationToken ct = default)
    {
        if (currentUser.Id is not { } userId) return false;
        var role = await organizations.GetRoleAsync(organizationId, userId, ct);
        if (role is null) return false;
        if (role is OrganizationRole.Admin or OrganizationRole.Owner) return true;
        var custom = await organizations.GetCustomRolesAsync(organizationId, userId, ct);
        return custom.Contains(CommerceRights.FinanceRole, StringComparer.OrdinalIgnoreCase)
            || custom.Contains(options.Value.Sellers.ExportRole, StringComparer.OrdinalIgnoreCase);
    }
}
