using System.Linq.Expressions;
using System.Security.Claims;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Organizations.Services;

/// <summary>
/// "The caller is a member of the row's organization, with the role the action requires"
/// (<c>docs/seo_analyzer_PRD.md</c> X11, D18), for every <see cref="IOrganizationOwned"/> type and for
/// <see cref="Organization"/> itself, on every path that asks row security.
/// </summary>
/// <remarks>
/// <para>
/// The predicate is <c>x.OrganizationId.In(ids)</c> (for an organization, <c>o.Id.In(ids)</c>): the ids
/// of the caller's memberships whose role is at least <see cref="RequiresOrganizationRoleAttribute.For"/>
/// the type and action, read once per request (<see cref="OrganizationMembershipReader"/>). It is pushed
/// into the query as <c>OrganizationId in ($p0, …)</c>, ANDed with the type's own rule and every other
/// policy (expiration, soft delete). Measured in §12 "Spike S13".
/// </para>
/// <para>
/// ⚠️ <c>In()</c>, never <c>Contains</c>: RavenDB's provider cannot translate <c>Contains</c> on an array
/// or list (CodeCoverage's <c>ApiTokenActions</c>). A row without <c>OrganizationId</c> matches no
/// <c>in</c>, so it is visible to nobody; that is the safe direction.
/// </para>
/// <para>
/// A caller with no membership of the needed role, and an anonymous caller, get a constant
/// <c>false</c>: no row, and a load answers the missing-row 404 (#453). Creating an
/// <see cref="Organization"/> is not row-limited (the creator becomes its Owner); who may create one at all
/// is the type right <c>New/Organization</c>. The system context (background work) is not limited.
/// </para>
/// <para>
/// A role requirement is met by the built-in role or by a custom role (<c>docs/commerce_PRD.md</c> X8,
/// <see cref="OrganizationRequirement"/>). For an <see cref="IOrganizationPublishable"/> type, <c>Query</c> and
/// <c>Read</c> become <c>OrganizationId in (…) || IsPublished == true</c> (just <c>IsPublished == true</c> for an
/// anonymous caller or a non-member): the one OR in row security, and it lives inside this policy, so the
/// composition across policies stays AND-only (<c>docs/guide-row-security.md</c>).
/// </para>
/// </remarks>
internal sealed class OrganizationRowPolicy(OrganizationMembershipReader memberships, CurrentOrganization current) : IRowFilterPolicy
{
    /// <summary>An organization id no document holds: random per process, never written anywhere.</summary>
    internal static readonly string[] NoOrganization = ["Organizations/none/" + Guid.NewGuid().ToString("N")];

    public bool AppliesTo(Type entityType)
        => typeof(IOrganizationOwned).IsAssignableFrom(entityType) || typeof(Organization).IsAssignableFrom(entityType);

    public bool BypassInSystemContext => true;

    public bool IsVisibilityDecision => true;

    public async ValueTask<LambdaExpression?> GetFilterAsync(RowPolicyContext context)
    {
        var isOrganization = typeof(Organization).IsAssignableFrom(context.EntityType);
        if (isOrganization && string.Equals(context.Action, "New", StringComparison.OrdinalIgnoreCase))
            return null;

        var ids = await AllowedOrganizationIdsAsync(context.User, context.EntityType, context.Action);

        // Publishable rows (X8): Query and Read also match every published row, for anyone. The OR stays
        // inside this policy's predicate; core ANDs it with every other rule as before.
        var orPublished = !isOrganization
            && typeof(IOrganizationPublishable).IsAssignableFrom(context.EntityType)
            && (string.Equals(context.Action, "Query", StringComparison.OrdinalIgnoreCase)
                || string.Equals(context.Action, "Read", StringComparison.OrdinalIgnoreCase));

        // The switcher (Q-M11a): a list of organization-owned rows shows the chosen organization's only.
        // Lists only: a detail page, an edit, a custom action and a sub-query (the audits of a site the
        // caller opened from elsewhere) keep every organization the role allows, and so does the
        // organization list itself, which the switcher picks from. It only ever narrows: a member working
        // in one organization does not see other organizations' published rows in their lists either.
        if (!isOrganization && !context.IsSubQuery
            && string.Equals(context.Action, "Query", StringComparison.OrdinalIgnoreCase)
            && ids.Length > 0
            && await current.GetIdAsync() is { } chosen)
        {
            ids = ids.Where(id => string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase)).ToArray();
            orPublished = false;
        }

        if (orPublished)
        {
            // `== true`, never the bare bool: an absent IsPublished field must not match (the safe direction).
            Expression<Func<IOrganizationPublishable, bool>> published = ids.Length == 0
                ? x => x.IsPublished == true
                : x => x.OrganizationId.In(ids) || x.IsPublished == true;
            return published;
        }

        // No organization: a constant false is evaluated in memory, which for a list means the whole
        // collection is read and dropped (S13: 170 ms for 10 000 rows, against 7–10 ms for a member).
        // The list gets an id no document can hold instead, so the database answers "none" from the
        // index. Only for Query: every write and single-row check keeps the constant false, so no row
        // can ever be created or moved into the sentinel.
        if (ids.Length == 0 && string.Equals(context.Action, "Query", StringComparison.OrdinalIgnoreCase))
            ids = NoOrganization;

        if (isOrganization)
        {
            Expression<Func<Organization, bool>> organization = ids.Length == 0 ? o => false : o => o.Id.In(ids);
            return organization;
        }

        Expression<Func<IOrganizationOwned, bool>> owned = ids.Length == 0 ? x => false : x => x.OrganizationId.In(ids);
        return owned;
    }

    /// <summary>The organizations in which <paramref name="user"/> may perform <paramref name="action"/> on <paramref name="entityType"/>.</summary>
    private async Task<string[]> AllowedOrganizationIdsAsync(ClaimsPrincipal? user, Type entityType, string action)
    {
        if (user?.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            return [];

        return await memberships.OrganizationsMeetingAsync(userId, RequiresOrganizationRoleAttribute.For(entityType, action));
    }
}
