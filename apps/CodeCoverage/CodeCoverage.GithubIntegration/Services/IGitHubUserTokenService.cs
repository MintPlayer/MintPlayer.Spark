namespace CodeCoverage.Services;

/// <summary>
/// Health of the signed-in user's GitHub credential after an operation.
/// <see cref="Unavailable"/> means "don't know" (GitHub unreachable, transient)
/// and callers must treat it like today's null semantics: degrade for this
/// request but neither cache nor clear anything. <see cref="ReauthRequired"/>
/// is authoritative: only a browser round-trip can mint a new token.
/// </summary>
public enum GitHubTokenState { Ok, ReauthRequired, Unavailable }

/// <summary>A usable access token (when <see cref="State"/> is Ok) or the reason there isn't one.</summary>
public sealed record GitHubUserToken(string? AccessToken, GitHubTokenState State);

/// <summary>
/// The single owner of "give me a working GitHub user token". Hides the whole
/// token lifecycle: reads the stored OAuth tokens, silently refreshes via the
/// refresh grant when the access token is expired or about to expire (GitHub
/// user tokens live 8 hours; refresh tokens 6 months, single-use, rotating),
/// and persists every token GitHub returns. Never throws into the caller.
/// </summary>
public interface IGitHubUserTokenService
{
    /// <param name="user">The signed-in user (from <c>UserManager.GetUserAsync</c>).</param>
    /// <param name="forceRefresh">
    /// Set when the caller just got a 401 with the token this service handed out:
    /// forces one refresh even if the stored expiry still looks comfortable.
    /// </param>
    Task<GitHubUserToken> GetAccessTokenAsync(MintPlayer.Spark.Authorization.Identity.SparkUser user, bool forceRefresh = false, CancellationToken cancellationToken = default);
}
