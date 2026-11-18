using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>The permission → security.json groups map of <see cref="AppsBuilder.MapPermission"/>.</summary>
internal sealed class AppPermissionGroups
{
    private readonly IReadOnlyDictionary<string, List<string>> map;

    public AppPermissionGroups(IReadOnlyDictionary<string, List<string>> map) => this.map = map;

    public IEnumerable<string> For(IReadOnlyCollection<string> scopes)
        => map.Where(entry => SparkAppManager.Covers(scopes, entry.Key)).SelectMany(entry => entry.Value);
}

/// <summary>
/// D17: an installation token is a member of the groups its scopes map to, so security.json's existing rights apply to
/// Apps. Only for a bearer token (not the ambient cookie) that carries an <c>installation_id</c>, which only the
/// identity provider's <see cref="InstallationTokenContributor"/> writes into a token.
/// </summary>
internal sealed partial class InstallationGroupMembership : IGroupMembershipProvider
{
    [Inject] private readonly IHttpContextAccessor accessor;
    [Inject] private readonly AppPermissionGroups groups;

    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext;
        if (context?.Features.Get<ISparkAuthenticatedSchemeFeature>()?.Scheme is not { IsAmbient: false }
            || context.User.FindFirst(InstallationTokenContributor.InstallationIdClaim) is null)
            return Task.FromResult<IEnumerable<string>>([]);

        var scopes = context.User.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IEnumerable<string>>(groups.For(scopes).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
