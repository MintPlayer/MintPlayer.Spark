namespace CodeCoverage.Services;

/// <summary>
/// Mirrors the signed-in user's GitHub visibility: which owners (their own login
/// plus every org/user whose app installation they can access) they may see.
/// GitHub is the authority — there is no app-local ACL.
/// </summary>
public interface IGitHubAccessService
{
    /// <summary>
    /// The owners the current user may see, plus the health of their GitHub
    /// credential. When the token is dead (<see cref="GitHubTokenState.ReauthRequired"/>)
    /// or GitHub is unreachable (<see cref="GitHubTokenState.Unavailable"/>),
    /// the list degrades to the user's own login — failure is not absence.
    /// </summary>
    Task<GitHubVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default);

    Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default);
    Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default);

    /// <summary>Drops the current user's cached owner list so the next call re-queries GitHub (manual resync).</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}

public sealed record GitHubVisibility(string[] Owners, GitHubTokenState TokenState);
