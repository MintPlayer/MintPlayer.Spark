using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Puts an approved developer in the group bound to the slot <c>identity-provider:developers</c>
/// (<c>docs/identity_provider_platform_PRD.md</c> D2): membership follows the developer status on the
/// user's document and is never seeded. Composed with the application's own membership by core
/// (<c>AddGroupMembershipProvider</c>), which asks once per request.
/// </summary>
/// <remarks>
/// The slot is resolved against the current <c>security.json</c> snapshot on every call, so a reload
/// of the bindings takes effect without a restart. An application that switched the library off
/// (<c>"libraries": { "identity-provider": false }</c>), or left the slot unbound, gets no group: its
/// rights are inert anyway.
/// </remarks>
internal sealed partial class OidcDeveloperMembership : IGroupMembershipProvider, IGroupIdMembershipProvider
{
    public const string DevelopersSlot = "identity-provider:developers";

    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ISecurityConfigurationLoader security;
    [Inject] private readonly OidcDevelopers developers;

    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<string>>([]);

    public async Task<IEnumerable<Guid>> GetCurrentUserGroupIdsAsync(CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return [];

        var ids = SparkSecurityFiles.ResolveGroup(security.GetConfiguration(), DevelopersSlot, out _);
        if (ids is not { Count: > 0 })
            return [];

        return developers.IsActive(await developers.GetAsync(userId, cancellationToken)) ? ids : [];
    }
}
