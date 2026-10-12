using System.Security.Claims;
using CodeCoverage.Forge;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Identity;

namespace CodeCoverage.Services;

/// <summary>
/// Puts the signed-in user in <c>security.json</c>'s <c>Administrators</c> group when one of their external logins is a
/// forge account listed in <c>Coverage:Administrators</c> (#504, D11): the host metrics dashboard and the mirrors list.
/// </summary>
/// <remarks>
/// <para>
/// <b>By the forge's stable user id, never by login.</b> An entry is <c>{provider}:{user id}</c>, e.g.
/// <c>github:9629574</c>, and is matched against the login's <c>ProviderKey</c>, which Spark's GitHub preset takes
/// from the account's numeric <c>id</c>. A login name can be renamed and then claimed by somebody else; the id
/// cannot. That is also why this is not a role on the user document: nothing a user does in this app (or at the
/// forge) can put them in the group, and taking someone out is a configuration change.
/// </para>
/// <para>
/// Composed with the claims provider by core (<c>AddGroupMembershipProvider</c>), which asks once per request; the
/// user load is skipped for anonymous callers and when nobody is configured.
/// </para>
/// </remarks>
internal sealed class CoverageAdministrators(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    IServiceProvider services) : IGroupMembershipProvider, IGroupIdMembershipProvider
{
    /// <summary>The configuration key: a list of <c>{provider}:{user id}</c>.</summary>
    public const string Section = "Coverage:Administrators";

    /// <summary>The <c>Administrators</c> group's id in <c>App_Data/security.json</c>.</summary>
    public static readonly Guid GroupId = Guid.Parse("c0e5ad00-0000-4000-8000-000000000001");

    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<string>>([]);

    public async Task<IEnumerable<Guid>> GetCurrentUserGroupIdsAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(principal.FindFirstValue(ClaimTypes.NameIdentifier)))
            return [];

        var administrators = Configured(configuration);
        if (administrators.Count == 0)
            return [];

        // Resolved here rather than injected: UserManager is scoped and pulls in the whole Identity stack, which a
        // request without a user never needs.
        var userManager = services.GetRequiredService<UserManager<SparkUser>>();
        var user = await userManager.GetUserAsync(principal);
        if (user?.Logins is not { Count: > 0 } logins)
            return [];

        return logins.Any(login => ForgeProviders.TryParse(login.LoginProvider, out var provider)
                                   && administrators.Contains((provider, login.ProviderKey)))
            ? [GroupId]
            : [];
    }

    /// <summary>The configured forge accounts; an entry that does not parse grants nothing.</summary>
    internal static HashSet<(EForgeProvider Provider, string UserId)> Configured(IConfiguration configuration)
    {
        var result = new HashSet<(EForgeProvider, string)>();
        foreach (var entry in configuration.GetSection(Section).Get<string[]>() ?? [])
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0 || !ForgeProviders.TryParse(entry[..separator], out var provider))
                continue;
            var userId = entry[(separator + 1)..].Trim();
            // Ids, not logins: GitHub and GitLab ids are numbers, Bitbucket's a braced UUID. Anything else would be a
            // login and is refused.
            if (userId.Length > 0 && (userId.All(char.IsAsciiDigit) || (userId.StartsWith('{') && Guid.TryParse(userId, out _))))
                result.Add((provider, userId));
        }
        return result;
    }
}
