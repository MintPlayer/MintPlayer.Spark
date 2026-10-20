using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// Who manages promotions: a seller's Owners and Admins and members with the <c>promotions</c> custom role (X8) manage
/// that seller's codes, sale prices, waiting lists and links; platform admins (<c>ManageSellers/Commerce</c>) manage
/// platform codes and every seller's.
/// </summary>
internal sealed class PromotionsAccess(
    ISparkCurrentUser currentUser,
    ISparkOrganizations organizations,
    IAccessControl accessControl,
    IOptionsMonitor<SparkCommercePromotionsOptions> options)
{
    private (bool Platform, IReadOnlyList<string> Organizations)? scope;

    public string? CallerId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    /// <summary>The caller's scope, computed once per request.</summary>
    public async Task<(bool Platform, IReadOnlyList<string> Organizations)> ScopeAsync(CancellationToken cancellationToken = default)
    {
        if (scope is { } known)
            return known;
        if (CallerId is not { } me)
            return (scope = (false, Array.Empty<string>())).Value;
        var platform = await accessControl.IsAllowedAsync(MintPlayer.Spark.Commerce.Services.CommerceRights.ManageSellers, cancellationToken);
        var role = options.CurrentValue.ManagerRole;
        var allowed = new List<string>();
        foreach (var (organizationId, builtIn) in await organizations.GetMembershipsAsync(me, cancellationToken))
        {
            if (builtIn >= OrganizationRole.Admin
                || (await organizations.GetCustomRolesAsync(organizationId, me, cancellationToken)).Contains(role, StringComparer.OrdinalIgnoreCase))
                allowed.Add(organizationId);
        }
        return (scope = (platform, (IReadOnlyList<string>)allowed)).Value;
    }

    /// <summary>Whether the caller manages promotions of <paramref name="organizationId"/> (null: the platform's own).</summary>
    public async Task<bool> MayManageAsync(string? organizationId, CancellationToken cancellationToken = default)
    {
        var (platform, allowed) = await ScopeAsync(cancellationToken);
        return platform || (organizationId is not null && allowed.Contains(organizationId, StringComparer.Ordinal));
    }

    /// <summary>The row rule of a read-only type with an <c>OrganizationId</c>: the caller's sellers, or everything for platform admins.</summary>
    public async Task<Expression<Func<T, bool>>> RowFilterAsync<T>(Expression<Func<T, string?>> seller, CancellationToken cancellationToken = default)
    {
        var (platform, allowed) = await ScopeAsync(cancellationToken);
        if (platform)
            return _ => true;
        if (allowed.Count == 0)
            return _ => false;
        var ids = allowed.ToArray();
        var inCall = Expression.Call(
            typeof(RavenQueryableExtensions).GetMethods().First(m => m.Name == nameof(RavenQueryableExtensions.In)
                && m.GetParameters() is [_, { ParameterType.IsArray: true }]).MakeGenericMethod(typeof(string)),
            seller.Body,
            Expression.Constant(ids));
        return Expression.Lambda<Func<T, bool>>(inCall, seller.Parameters);
    }
}
