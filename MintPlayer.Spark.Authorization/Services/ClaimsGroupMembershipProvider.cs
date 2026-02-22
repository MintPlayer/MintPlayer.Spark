using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Default implementation that reads groups from "group" or "groups" claims.
/// Replace this with your own implementation for custom group membership resolution
/// (e.g., from ASP.NET Identity roles, database lookup, etc.).
/// </summary>
[Register(typeof(IGroupMembershipProvider), ServiceLifetime.Scoped)]
internal partial class ClaimsGroupMembershipProvider : IGroupMembershipProvider
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>
    /// Known claim types that may contain group information.
    /// </summary>
    private static readonly string[] GroupClaimTypes =
    [
        "group",
        "groups",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid",
        "http://schemas.xmlsoap.org/claims/Group"
    ];

    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
    {
        var user = httpContextAccessor.HttpContext?.User;

        if (user == null || user.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(Enumerable.Empty<string>());
        }

        var groups = user.Claims
            .Where(c => GroupClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .Distinct()
            .ToList();

        return Task.FromResult<IEnumerable<string>>(groups);
    }
}
