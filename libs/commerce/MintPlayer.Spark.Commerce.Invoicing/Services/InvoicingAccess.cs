using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Invoicing.Services;

/// <summary>
/// Who sees invoices: the buyer their own (by account); a seller's Owners, Admins and <c>finance</c> members the
/// seller's (issued by it, or the platform's fee invoices to it); the same people of the platform organization
/// (<c>Spark:Commerce:Payments:PlatformOrganizationId</c>) every document. Anyone else sees none (the missing-row 404).
/// Exports need the seller scope (or the platform's).
/// </summary>
internal sealed class InvoicingAccess(
    ISparkCurrentUser currentUser,
    ISparkOrganizations organizations,
    IOptionsMonitor<SparkCommerceInvoicingOptions> options,
    IOptionsMonitor<SparkCommercePaymentsOptions> payments)
{
    private (bool PlatformWide, List<string> Organizations)? scope;

    public string? CallerId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    public async Task<(bool PlatformWide, List<string> Organizations)> ScopeAsync(CancellationToken cancellationToken = default)
    {
        if (scope is { } known)
            return known;
        if (CallerId is not { } me)
            return (scope = (false, [])).Value;
        var role = options.CurrentValue.FinanceRole;
        var allowed = new List<string>();
        foreach (var (organizationId, builtIn) in await organizations.GetMembershipsAsync(me, cancellationToken))
        {
            if (builtIn >= OrganizationRole.Admin
                || (await organizations.GetCustomRolesAsync(organizationId, me, cancellationToken)).Contains(role, StringComparer.OrdinalIgnoreCase))
                allowed.Add(organizationId);
        }
        var platformWide = payments.CurrentValue.PlatformOrganizationId is { Length: > 0 } platform && allowed.Contains(platform);
        return (scope = (platformWide, allowed)).Value;
    }

    /// <summary>The row rule of <see cref="Invoice"/>.</summary>
    public async Task<Expression<Func<Invoice, bool>>> InvoiceFilterAsync(CancellationToken cancellationToken = default)
    {
        var me = CallerId;
        var (platformWide, ids) = await ScopeAsync(cancellationToken);
        if (platformWide)
            return _ => true;
        if (me is null)
            return _ => false;
        if (ids.Count == 0)
            return i => i.BuyerUserId == me;
        var sellers = ids.ToArray();
        return i => i.BuyerUserId == me || i.SellerOrganizationId.In(sellers);
    }

    /// <summary>The row rule of a seller-keyed type (transmissions).</summary>
    public async Task<Expression<Func<InvoiceTransmission, bool>>> TransmissionFilterAsync(CancellationToken cancellationToken = default)
    {
        var (platformWide, ids) = await ScopeAsync(cancellationToken);
        if (platformWide)
            return _ => true;
        if (ids.Count == 0)
            return _ => false;
        var sellers = ids.ToArray();
        return t => t.SellerOrganizationId.In(sellers);
    }

    /// <summary>Whether the caller may read <paramref name="invoice"/>.</summary>
    public async Task<bool> MaySeeAsync(Invoice invoice, CancellationToken cancellationToken = default)
    {
        var (platformWide, ids) = await ScopeAsync(cancellationToken);
        return platformWide
            || (CallerId is { } me && invoice.BuyerUserId == me)
            || (invoice.SellerOrganizationId is { } seller && ids.Contains(seller));
    }

    /// <summary>Whether the caller may export for <paramref name="issuerKey"/> (a seller id, or <c>platform</c> for the platform's own).</summary>
    public async Task<bool> MayExportAsync(string issuerKey, CancellationToken cancellationToken = default)
    {
        var (platformWide, ids) = await ScopeAsync(cancellationToken);
        return platformWide || (issuerKey != InvoiceIssuers.PlatformKey && ids.Contains(issuerKey));
    }

    /// <summary>Platform-wide exports (DAC7, all sellers).</summary>
    public async Task<bool> IsPlatformAsync(CancellationToken cancellationToken = default) => (await ScopeAsync(cancellationToken)).PlatformWide;
}
