using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// Permissions the app now asks for that the account has not accepted yet (merge-queue PRD O17).
/// </summary>
/// <remarks>
/// A capability of its own rather than a member of <see cref="IForgeIntegration"/>: only GitHub has an
/// install-then-accept-upgrades step, and a forge without one simply registers no implementation.
/// Callers pick the implementation by <see cref="Provider"/>.
/// </remarks>
public interface IForgeInstallationPermissions
{
    EForgeProvider Provider { get; }

    /// <summary>
    /// The pending permissions of the account's installation, or <c>null</c> when nothing can be said:
    /// the account has no installation, the app is not configured, or the forge could not be reached.
    /// Never throws for a forge failure; the page that shows the notice must keep working without it.
    /// </summary>
    Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default);
}

/// <summary>The installation's page on the forge, and what still waits for acceptance there.</summary>
/// <param name="HtmlUrl">
/// The installation's own settings page as the forge reports it (<c>html_url</c>), where the account
/// accepts the new permissions. Never built from the login: user and organization installations live
/// under different paths.
/// </param>
public sealed record PendingInstallationPermissions(string HtmlUrl, IReadOnlyList<PendingPermission> Pending);

/// <param name="Name">The forge's permission key, e.g. <c>actions</c> or <c>pull_requests</c>.</param>
/// <param name="DisplayName">The key for people: <c>Pull requests</c>.</param>
/// <param name="Declared">The level the app asks for.</param>
/// <param name="Accepted">The level the installation granted; <c>null</c> when it granted none.</param>
public sealed record PendingPermission(string Name, string DisplayName, string Declared, string? Accepted);
