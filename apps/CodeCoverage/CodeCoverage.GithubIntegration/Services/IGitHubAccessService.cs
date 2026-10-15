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

    /// <summary>
    /// The signed-in user's own GitHub login, as GitHub returned it for the user's own token (<c>GET /user</c>), never a
    /// name the user can edit; whether a GitHub sign-in is linked at all; and the token's health when one is. Whatever
    /// the app does on GitHub in the user's name acts as this login (the merge queue's button, merge-queue PRD O8, T1).
    /// </summary>
    Task<GitHubVerifiedLogin> GetVerifiedLoginAsync(CancellationToken cancellationToken = default);
}

public sealed record GitHubVisibility(string[] Owners, GitHubTokenState TokenState);

/// <summary>The signed-in user's GitHub login as GitHub verified it; <see cref="Login"/> is null when there is none to give.</summary>
/// <param name="Linked">The user's account has a GitHub sign-in at all; false for an anonymous caller too.</param>
/// <param name="TokenState">Why a linked user has no login: <see cref="GitHubTokenState.ReauthRequired"/> or <see cref="GitHubTokenState.Unavailable"/>.</param>
public sealed record GitHubVerifiedLogin(string? Login, bool Linked, GitHubTokenState TokenState);
