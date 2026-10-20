namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// Links a seller's own provider account in <see cref="MerchantMode.ConnectedSellers"/> mode (D5, D21, spike S6):
/// Mollie Connect (OAuth 2 authorization code: the seller signs in at Mollie and grants the platform access) or Stripe
/// Connect (a connected account the platform creates, then an Account Link where the seller completes Stripe's hosted
/// onboarding). Selling stays blocked until <see cref="GetAccountAsync"/> reports <see cref="KycStatus.Verified"/>.
/// </summary>
public interface IConnectOnboardingProvider
{
    /// <summary>The provider (<c>mollie</c>, <c>stripe</c>).</summary>
    string Provider { get; }

    /// <summary>
    /// Starts (or resumes) onboarding: answers the URL to send the seller to. <paramref name="state"/> is an opaque,
    /// single-use value the callback must return unchanged (CSRF protection of the OAuth flow, RFC 6749 §10.12).
    /// </summary>
    /// <param name="existingAccountId">The seller's account when one was created before (Stripe resumes it); null otherwise.</param>
    Task<ConnectOnboardingStart> StartAsync(string sellerOrganizationId, string? existingAccountId, string returnUrl, string refreshUrl, string state, CancellationToken cancellationToken);

    /// <summary>
    /// Completes the redirect back from the provider: Mollie exchanges <paramref name="code"/> for tokens; Stripe has
    /// nothing to exchange (the account was created in <see cref="StartAsync"/>) and only reads the account.
    /// </summary>
    Task<ConnectOnboardingResult> CompleteAsync(string? accountId, string? code, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Reads the account's verification state (KYC) now.</summary>
    Task<ConnectedAccountSnapshot> GetAccountAsync(MerchantContext merchant, CancellationToken cancellationToken);

    /// <summary>Renews an expired access token (Mollie: access tokens live one hour; the refresh token is kept). Null when the provider uses none.</summary>
    Task<ConnectTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>Where to send the seller, and the account created for them if any (Stripe).</summary>
public sealed record ConnectOnboardingStart(string RedirectUrl, string? AccountId);

/// <summary>A linked account: its id, its first state and, for OAuth providers, the tokens.</summary>
public sealed record ConnectOnboardingResult(string AccountId, ConnectedAccountSnapshot Account, ConnectTokens? Tokens, string? ProfileId);

/// <summary>OAuth tokens (Mollie Connect). Credentials: stored encrypted (<see cref="Services.ConnectTokenProtector"/>), never logged.</summary>
public sealed class ConnectTokens
{
    public ConnectTokens(string accessToken, string? refreshToken, DateTimeOffset expiresAt)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAt = expiresAt;
    }

    public string AccessToken { get; }

    public string? RefreshToken { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <inheritdoc />
    public override string ToString() => $"tokens(expires {ExpiresAt:O})";
}
