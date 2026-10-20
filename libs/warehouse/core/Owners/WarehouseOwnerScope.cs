using MintPlayer.SourceGenerators.Attributes;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Warehouse.Owners;

/// <summary>
/// Which owners the current caller may see of one type (WD3, WI5), for the library's explicit owner filters: raw session
/// queries are not row-filtered (<c>IOrganizationOwned.cs:21-22</c>), so every library query passes this set on.
/// </summary>
public interface IWarehouseOwnerScope
{
    /// <summary>
    /// <see langword="null"/>: every owner (the caller holds <c>AllOrganizations/{typeName}</c>, e.g. through the
    /// <c>warehouse:staff</c> slot, or background work). Otherwise the organizations the caller is a member of; an empty
    /// set sees nothing.
    /// </summary>
    /// <param name="typeName">The persistent-object type the rows are, e.g. <c>StockBalance</c>.</param>
    Task<IReadOnlyCollection<string>?> VisibleOwnersAsync(string typeName, CancellationToken cancellationToken = default);
}

/// <summary>
/// The same rule as Organizations' row policy (lead decision 2026-10-20): the cross-organization right
/// <see cref="OrganizationRights.AllOrganizations"/> per type, granted in <c>security.json</c> (the library grants it to
/// its staff and supervisor slots); everyone else sees the organizations they belong to.
/// </summary>
internal sealed partial class WarehouseOwnerScope : IWarehouseOwnerScope
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly IPermissionService permissions;

    public async Task<IReadOnlyCollection<string>?> VisibleOwnersAsync(string typeName, CancellationToken cancellationToken = default)
    {
        if (SparkSystemContext.IsSystemContext(httpContextAccessor))
            return null;
        if (currentUser.Id is not { Length: > 0 } userId)
            return [];
        if (await permissions.IsAllowedAsync(OrganizationRights.AllOrganizations, typeName, cancellationToken))
            return null;
        return (await organizations.GetMembershipsAsync(userId, cancellationToken)).Keys.ToList();
    }
}
