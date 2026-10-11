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
/// The applications screen (<c>docs/identity_provider_platform_PRD.md</c> D2, D3, Q1 = A): the generic
/// PersistentObject screens, shipped by the library with <c>Custom.Applications</c> as their query.
/// </summary>
/// <remarks>
/// <b>Members only.</b> A developer sees and edits the applications they are an Admin or Developer
/// of; testers have no portal access (D3). An identity-provider administrator, who holds
/// <c>ManageAll/IdentityProvider</c> through the slot <c>identity-provider:administrators</c>, sees
/// every application. The rule is <see cref="GetRowFilterAsync"/>, which the framework applies to
/// every path: lists, detail, edit, delete and, as a WITH CHECK, create. A library cannot ship a
/// row policy in JSON (spike S1), so it lives here.
/// </remarks>
public partial class OidcApplicationActions : DefaultPersistentObjectActions<OidcApplication>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IAccessControl accessControl;

    /// <summary>The reserved right that lifts the members-only filter.</summary>
    public const string ManageAllResource = "ManageAll/IdentityProvider";

    /// <summary>Every application, narrowed per caller by <see cref="GetRowFilterAsync"/>.</summary>
    public IRavenQueryable<OidcApplication> Applications() => session.Query<OidcApplication>();

    public override async Task<Expression<Func<OidcApplication, bool>>?> GetRowFilterAsync(string action)
    {
        if (await accessControl.IsAllowedAsync(ManageAllResource))
            return null;

        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return app => false;

        // A new application has no members yet; the interceptor makes its creator the first Admin
        // before the WITH CHECK runs, so the creator passes this rule like any member.
        return app => app.Members.Any(m =>
            m.UserId == userId
            && m.Status == OidcMemberStatuses.Active
            && (m.Role == OidcMemberRoles.Admin || m.Role == OidcMemberRoles.Developer));
    }
}
