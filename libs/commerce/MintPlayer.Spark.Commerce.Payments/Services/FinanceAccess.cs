using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Who sees the money rows (disputes, linked accounts, the fee ledger, settlement lines, the reconciliation queue):
/// a seller's Owners and Admins and its <c>finance</c> members see that seller's rows; the same people of
/// <see cref="SparkCommercePaymentsOptions.PlatformOrganizationId"/> see every row, the platform's own included.
/// Anyone else sees none (the missing-row 404). Rows carry <c>SellerOrganizationId</c> (null for the platform's own).
/// </summary>
internal sealed partial class FinanceAccess
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly IOptionsMonitor<SparkCommercePaymentsOptions> options;

    private FinanceScope? scope;

    /// <summary>The caller's user id; null when anonymous.</summary>
    public string? CallerId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    /// <summary>The caller's scope, computed once per request.</summary>
    public async Task<FinanceScope> ScopeAsync(CancellationToken cancellationToken = default)
    {
        if (scope is not null)
            return scope;
        if (CallerId is not { } me)
            return scope = FinanceScope.None;

        var role = options.CurrentValue.Disputes.NotifyRole;
        var memberships = await organizations.GetMembershipsAsync(me, cancellationToken);
        var allowed = new List<string>();
        foreach (var (organizationId, builtIn) in memberships)
        {
            if (builtIn >= OrganizationRole.Admin
                || (await organizations.GetCustomRolesAsync(organizationId, me, cancellationToken)).Contains(role, StringComparer.OrdinalIgnoreCase))
                allowed.Add(organizationId);
        }
        var platformWide = options.CurrentValue.PlatformOrganizationId is { Length: > 0 } platform && allowed.Contains(platform);
        return scope = new FinanceScope(platformWide, allowed);
    }

    /// <summary>
    /// The row rule for a type with a <c>SellerOrganizationId</c>, given as the member selector. Rebuilt per type
    /// because RavenDB's LINQ provider needs the member access on the queried type itself.
    /// </summary>
    public async Task<Expression<Func<T, bool>>> RowFilterAsync<T>(Expression<Func<T, string?>> seller, CancellationToken cancellationToken = default)
    {
        var s = await ScopeAsync(cancellationToken);
        if (s.PlatformWide)
            return _ => true;
        if (s.Organizations.Count == 0)
            return _ => false;

        var ids = s.Organizations.ToArray();
        // seller(row).In(ids)
        var inCall = Expression.Call(
            typeof(RavenQueryableExtensions).GetMethods().First(m => m.Name == nameof(RavenQueryableExtensions.In)
                && m.GetParameters() is [_, { ParameterType.IsArray: true }]).MakeGenericMethod(typeof(string)),
            seller.Body,
            Expression.Constant(ids));
        return Expression.Lambda<Func<T, bool>>(inCall, seller.Parameters);
    }

    /// <summary>The rule for one loaded row.</summary>
    public async Task<bool> MaySeeAsync(string? sellerOrganizationId, CancellationToken cancellationToken = default)
    {
        var s = await ScopeAsync(cancellationToken);
        return s.PlatformWide || (sellerOrganizationId is not null && s.Organizations.Contains(sellerOrganizationId));
    }
}

/// <summary>What a caller may see: everything, or the rows of these sellers.</summary>
internal sealed record FinanceScope(bool PlatformWide, IReadOnlyList<string> Organizations)
{
    public static FinanceScope None { get; } = new(false, []);
}
