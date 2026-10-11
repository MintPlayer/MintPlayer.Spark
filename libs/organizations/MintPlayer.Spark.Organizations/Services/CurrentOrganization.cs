using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Organizations.Services;

/// <summary>
/// The organization a user picked in the switcher (<c>docs/seo_analyzer_PRD.md</c> Q-M11a): lists of
/// <see cref="IOrganizationOwned"/> rows show only that organization's, and a new row lands there when its
/// form leaves the organization empty. No choice is "All organizations", the default.
/// </summary>
/// <remarks>
/// <para>
/// The choice is a cookie (<see cref="CookieName"/>, per browser, like the theme), never trusted: every
/// request checks it against the caller's memberships, read once per request anyway by the row policy
/// (<see cref="OrganizationMembershipReader"/>). A cookie naming an organization the caller is not (or no
/// longer) a member of counts as no choice, so a removed member, or the next user on a shared browser,
/// simply sees all of their own organizations. The cookie can only narrow what the row policy already allows.
/// </para>
/// <para>
/// Nothing is stored server-side: switching writes no document and adds no load per request.
/// </para>
/// </remarks>
internal sealed class CurrentOrganization(IHttpContextAccessor httpContextAccessor, OrganizationMembershipReader memberships, IDocumentStore store)
{
    /// <summary>The cookie that holds the chosen organization's id.</summary>
    internal const string CookieName = "spark-organization";

    /// <summary>The chosen organization's id when the caller is still a member of it; null for "All organizations".</summary>
    public async Task<string?> GetIdAsync(CancellationToken cancellationToken = default)
    {
        var http = httpContextAccessor.HttpContext;
        if (http?.Request.Cookies[CookieName] is not { Length: > 0 } chosen
            || http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            return null;

        var roles = await memberships.GetAsync(userId, cancellationToken);
        return roles.Keys.FirstOrDefault(id => string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The organization a new <paramref name="entityType"/> row lands in when its form names none: the chosen one
    /// where the caller holds the type's <c>New</c> role, else <paramref name="userId"/>'s personal organization (D21).
    /// </summary>
    public async Task<string> DefaultForNewAsync(Type entityType, string userId, CancellationToken cancellationToken = default)
    {
        if (await GetIdAsync(cancellationToken) is { } chosen)
        {
            var roles = await memberships.GetAsync(userId, cancellationToken);
            if (roles.TryGetValue(chosen, out var role) && role >= RequiresOrganizationRoleAttribute.For(entityType, "New"))
                return chosen;
        }
        return Organization.PersonalIdFor(userId);
    }

    /// <summary>What the switcher shows: the choice and every organization the caller belongs to, personal first, then by name.</summary>
    public async Task<CurrentOrganizationResponse> DescribeAsync(string userId, CancellationToken cancellationToken = default)
    {
        var roles = await memberships.GetAsync(userId, cancellationToken);
        using var session = store.OpenAsyncSession();
        var organizations = roles.Count == 0
            ? new Dictionary<string, Organization>()
            : await session.LoadAsync<Organization>(roles.Keys, cancellationToken);

        var choices = organizations.Values
            .Where(o => o?.Id is not null)
            .Select(o => new OrganizationChoice(o!.Id!, o.Name, o.IsPersonal, roles[o.Id!]))
            .OrderByDescending(o => o.IsPersonal)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new CurrentOrganizationResponse(await GetIdAsync(cancellationToken), choices);
    }

    /// <summary>Remembers <paramref name="organizationId"/> for this browser, or forgets the choice when null.</summary>
    public void Remember(HttpContext http, string? organizationId)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        };
        if (organizationId is null)
        {
            http.Response.Cookies.Delete(CookieName, options);
            return;
        }
        options.MaxAge = TimeSpan.FromDays(365);
        http.Response.Cookies.Append(CookieName, organizationId, options);
    }

    /// <summary>Whether this request carries a choice at all (valid or not).</summary>
    public bool HasCookie(HttpContext http) => http.Request.Cookies.ContainsKey(CookieName);
}

/// <summary>One organization the switcher offers.</summary>
public sealed record OrganizationChoice(string Id, string Name, bool IsPersonal, OrganizationRole Role);

/// <summary>
/// What <c>GET /spark/organizations/current</c> answers: the chosen organization (null = all of them) and the
/// caller's organizations.
/// </summary>
public sealed record CurrentOrganizationResponse(string? Current, OrganizationChoice[] Organizations);
