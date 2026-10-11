using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>
/// The resources screen (<c>docs/identity_provider_platform_PRD.md</c> D1, D4): identity resources and
/// APIs. Every signed-in developer may list them, because an application's scopes are chosen from
/// them; only an API's owners, or an identity-provider administrator, may change one.
/// </summary>
public partial class OidcResourceActions : DefaultPersistentObjectActions<OidcResource>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IAccessControl accessControl;

    /// <summary>Every resource, narrowed for writes by <see cref="GetRowFilterAsync"/>.</summary>
    public IRavenQueryable<OidcResource> Resources() => session.Query<OidcResource>();

    public override async Task<Expression<Func<OidcResource, bool>>?> GetRowFilterAsync(string action)
    {
        if (await accessControl.IsAllowedAsync(OidcApplicationActions.ManageAllResource))
            return null;

        // Reading is open to whoever holds the type right: the scopes are the menu an application
        // picks from, and discovery publishes most of them anyway.
        if (action is "Query" or "Read")
            return null;

        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return resource => false;

        // Identity resources are the provider's own (openid, profile, email): administrators only.
        // A new API passes because the interceptor makes its creator its first owner first.
        return resource => resource.Kind == OidcResourceKinds.Api && resource.Owners.Contains(userId);
    }
}
