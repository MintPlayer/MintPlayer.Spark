using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Who may authorize an application, and with which of its scopes
/// (<c>docs/identity_provider_platform_PRD.md</c> D3, D4).
/// </summary>
public static class OidcApplicationAccess
{
    /// <summary>
    /// Whether <paramref name="userId"/> may authorize <paramref name="app"/> at all. In Development
    /// mode only its team can: Admins, Developers and Testers who accepted their invitation.
    /// </summary>
    public static bool MayAuthorize(OidcApplication app, string userId)
        => app.Mode == OidcApplicationModes.Live || app.ActiveMember(userId) is not null;

    /// <summary>
    /// The requested scopes <paramref name="userId"/> can get from <paramref name="app"/>. A rejected
    /// scope is never available. A scope still pending its owner's approval is available to the
    /// application's own team (Development needs no approval), and to nobody else.
    /// </summary>
    public static List<string> AvailableScopes(OidcApplication app, string userId, IEnumerable<string> requested)
    {
        var isMember = app.ActiveMember(userId) is not null;
        return requested
            .Where(name => app.Scopes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } scope
                && (scope.Status == OidcScopeStatuses.Approved || (isMember && scope.Status == OidcScopeStatuses.Pending)))
            .ToList();
    }
}
