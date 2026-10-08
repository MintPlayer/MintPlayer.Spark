using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Actions;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The per-application role checks the portal's actions make (<c>docs/identity_provider_platform_PRD.md</c>
/// D3). The type rights and the members-only row filter decide who reaches an application; these decide
/// what each role may do there. An identity-provider administrator (<c>ManageAll/IdentityProvider</c>)
/// may do everything.
/// </summary>
internal sealed partial class OidcPortalAccess
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IAccessControl accessControl;

    public string? UserId => httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public Task<bool> IsAdministratorAsync() => accessControl.IsAllowedAsync(OidcApplicationActions.ManageAllResource);

    /// <summary>Whether the caller is an Admin of <paramref name="app"/>, or an identity-provider administrator.</summary>
    public async Task<bool> IsApplicationAdminAsync(OidcApplication app)
        => app.ActiveMember(UserId) is { Role: OidcMemberRoles.Admin } || await IsAdministratorAsync();

    /// <summary>Whether the caller owns the API resource, or is an identity-provider administrator.</summary>
    public async Task<bool> OwnsAsync(OidcResource resource)
        => (UserId is { } userId && resource.Owners.Contains(userId)) || await IsAdministratorAsync();
}
