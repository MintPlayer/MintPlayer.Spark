using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// Who sees and handles orders: the buyer their own; a seller's Owners, Admins and members holding <c>finance</c> or the
/// back-office role (GB7) the seller's; the same people of the platform organization
/// (<c>Spark:Commerce:Payments:PlatformOrganizationId</c>) every order. Anyone else sees none (the missing-row 404).
/// </summary>
internal sealed class OrderAccess(
    ISparkCurrentUser currentUser,
    ISparkOrganizations organizations,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    IOptionsMonitor<SparkCommercePaymentsOptions> payments)
{
    private (bool PlatformWide, List<string> Organizations)? scope;

    public string? CallerId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    private async Task<(bool PlatformWide, List<string> Organizations)> ScopeAsync(CancellationToken cancellationToken)
    {
        if (scope is { } known)
            return known;
        if (CallerId is not { } me)
            return (scope = (false, [])).Value;
        var roles = new[] { options.CurrentValue.BackOfficeRole, options.CurrentValue.MarkPaidRole };
        var allowed = new List<string>();
        foreach (var (organizationId, builtIn) in await organizations.GetMembershipsAsync(me, cancellationToken))
        {
            if (builtIn >= OrganizationRole.Admin
                || (await organizations.GetCustomRolesAsync(organizationId, me, cancellationToken)).Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                allowed.Add(organizationId);
        }
        var platformWide = payments.CurrentValue.PlatformOrganizationId is { Length: > 0 } platform && allowed.Contains(platform);
        return (scope = (platformWide, allowed)).Value;
    }

    /// <summary>The row rule of <c>CheckoutOrder</c>.</summary>
    public async Task<Expression<Func<CheckoutOrder, bool>>> RowFilterAsync(CancellationToken cancellationToken = default)
    {
        var me = CallerId;
        var (platformWide, ids) = await ScopeAsync(cancellationToken);
        if (platformWide)
            return _ => true;
        if (me is null)
            return _ => false;
        if (ids.Count == 0)
            return o => o.BuyerUserId == me;
        var sellers = ids.ToArray();
        return o => o.BuyerUserId == me || o.Sellers.ContainsAny(sellers);
    }

    /// <summary>Whether the caller holds <paramref name="role"/> (or is Owner/Admin) in <paramref name="organizationId"/>, or in the platform organization.</summary>
    public async Task<bool> HasRoleAsync(string? organizationId, string role, CancellationToken cancellationToken = default)
    {
        if (CallerId is not { } me)
            return false;
        foreach (var candidate in new[] { organizationId, payments.CurrentValue.PlatformOrganizationId }.Where(o => o is { Length: > 0 }).Distinct())
        {
            var builtIn = await organizations.GetRoleAsync(candidate!, me, cancellationToken);
            if (builtIn is null)
                continue;
            if (builtIn >= OrganizationRole.Admin
                || (await organizations.GetCustomRolesAsync(candidate!, me, cancellationToken)).Contains(role, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Whether the caller may handle <paramref name="order"/> in <paramref name="role"/>: in one of its sellers (or the platform's).</summary>
    public async Task<bool> MayHandleAsync(CheckoutOrder order, string role, CancellationToken cancellationToken = default)
    {
        if (order.Sellers.Count == 0)
            return await HasRoleAsync(null, role, cancellationToken);
        foreach (var seller in order.Sellers)
            if (await HasRoleAsync(seller, role, cancellationToken))
                return true;
        return false;
    }
}
