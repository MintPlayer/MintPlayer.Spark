namespace MintPlayer.Spark.Authorization.OneShotOAuth;

/// <summary>
/// A token from an OAuth client, borrowed by one custom action and revoked when the action is done
/// (#554): authorized in a popup, never stored, never a sign-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only inside a custom action</b>, because it is a client-method retry
/// (<c>IRetryAccessor.Invoke("oauth.authorize")</c>). Pass 1 hands the browser the authorization URL and
/// throws the retry; the browser opens it in a popup, the provider redirects to
/// <c>/spark/auth/oauth-callback</c>, which exchanges the code (PKCE S256) and keeps the token in memory
/// for at most five minutes, bound to the signed-in user. Pass 2 takes it out, once.
/// </para>
/// <para>
/// ⚠️ <b>The action re-runs from the top on pass 2</b>, so everything before this call must be side-effect
/// free. Call it after the last confirmation the action asks for, so a token is only minted for work
/// the user has agreed to.
/// </para>
/// <para>
/// Dispose the lease in a <c>finally</c> (<c>await using</c>): that revokes the token.
/// </para>
/// </remarks>
public interface ISparkOneShotOAuth
{
    /// <summary>
    /// Authorizes <paramref name="client"/> for <paramref name="scopes"/> and returns the token lease,
    /// or why there is none.
    /// </summary>
    /// <exception cref="InvalidOperationException">No client is registered under <paramref name="client"/>.</exception>
    Task<SparkOneShotOAuthOutcome> AuthorizeAsync(string client, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken = default);
}

/// <summary>A borrowed token. Disposing it revokes the token.</summary>
public interface ISparkOneShotOAuthLease : IAsyncDisposable
{
    /// <summary>The access token. Never log it, store it or send it to the browser.</summary>
    string AccessToken { get; }

    /// <summary>The scopes the provider granted, as it reported them, or <see langword="null"/>.</summary>
    string? GrantedScope { get; }
}

/// <summary>The lease, or the reason there is none (<see cref="SparkOneShotOAuthErrors"/>).</summary>
public sealed record SparkOneShotOAuthOutcome(ISparkOneShotOAuthLease? Lease, string? Error)
{
    /// <summary>Whether a token was obtained.</summary>
    public bool Succeeded => Lease is not null;
}

/// <summary>Why <see cref="ISparkOneShotOAuth.AuthorizeAsync"/> has no lease.</summary>
public static class SparkOneShotOAuthErrors
{
    /// <summary>The user closed or dismissed the step, or the browser cannot run it.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The browser blocked the popup.</summary>
    public const string PopupBlocked = "popup_blocked";

    /// <summary>The user refused the provider's consent.</summary>
    public const string Denied = "denied";

    /// <summary>The five minutes passed, or the token was already taken.</summary>
    public const string Expired = "expired";

    /// <summary>Anything else: a failed code exchange, another user's handle, a tampered state.</summary>
    public const string Failed = "failed";
}
