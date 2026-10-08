using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>One pending scope approval: an application asking for an API scope its team does not own (D4).</summary>
/// <param name="Id"><c>{applicationId}|{scope}</c>, which the row actions receive as the selection.</param>
public sealed record OidcScopeApproval(string Id, string Application, string Scope, string Resource, DateTime? RequestedAt);

/// <summary>
/// Backs <c>Custom.ScopeApprovals</c>: the pending scope approvals the caller may decide, i.e. those on
/// APIs they own, or all of them for an identity-provider administrator (D4).
/// </summary>
internal sealed partial class OidcScopeApprovalActions : ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "Rows are filtered here, before they leave: a pending scope is listed only when the caller owns its API " +
        "resource (OidcResource.Owners), or holds ManageAll/IdentityProvider. The row carries the application's " +
        "display name and the scope, nothing else of the application.";

    public const string QueryAlias = "oidc-scope-approvals";

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;

    public async Task<IQueryable<OidcScopeApproval>> ScopeApprovals()
    {
        var apps = await session.Query<OidcApplication>()
            .Where(a => a.Scopes.Any(s => s.Status == OidcScopeStatuses.Pending))
            .ToListAsync();

        var rows = new List<OidcScopeApproval>();
        var resources = new Dictionary<string, OidcResource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            foreach (var scope in app.Scopes.Where(s => s.Status == OidcScopeStatuses.Pending))
            {
                if (OidcScopeCatalog.ApiResourceNameOf(scope.Name) is not { } apiName) continue;
                if (!resources.TryGetValue(apiName, out var api))
                    resources[apiName] = api = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(apiName));
                if (api is null || !await access.OwnsAsync(api)) continue;
                rows.Add(new OidcScopeApproval($"{app.Id}|{scope.Name}", app.DisplayName, scope.Name, api.Name, scope.Review?.RequestedAt));
            }
        }
        return rows.AsQueryable();
    }
}

/// <summary>One pending request to switch an application to Live (D4, <c>Apps:RequireReviewToGoLive</c>).</summary>
public sealed record OidcGoLiveReview(string Id, string Application, string ClientId, DateTime? RequestedAt);

/// <summary>Backs <c>Custom.GoLiveReviews</c>: applications waiting for an administrator's go-live decision.</summary>
internal sealed partial class OidcGoLiveReviewActions : ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "Only Query/OidcGoLiveReview reaches these rows, and the library grants it to identity-provider:administrators " +
        "alone. A row is an application's id, display name and client id.";

    public const string QueryAlias = "oidc-golive-reviews";

    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<IQueryable<OidcGoLiveReview>> GoLiveReviews()
    {
        var apps = await session.Query<OidcApplication>()
            .Where(a => a.GoLiveReview != null && a.GoLiveReview.Status == OidcScopeStatuses.Pending)
            .ToListAsync();
        return apps.Select(a => new OidcGoLiveReview(a.Id!, a.DisplayName, a.ClientId, a.GoLiveReview?.RequestedAt)).AsQueryable();
    }
}
