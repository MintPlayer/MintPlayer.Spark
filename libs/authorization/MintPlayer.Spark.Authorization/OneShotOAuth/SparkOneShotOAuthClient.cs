namespace MintPlayer.Spark.Authorization.OneShotOAuth;

/// <summary>
/// One OAuth 2.0 client a custom action may borrow a token from for the length of one action
/// (<see cref="ISparkOneShotOAuth"/>). Registered by name with
/// <see cref="SparkOneShotOAuthExtensions.AddOneShotOAuthClient"/>.
/// </summary>
/// <remarks>
/// Nothing here is provider-specific: the provider's authorization and token endpoints, and how it
/// revokes a token, are the application's to give. GitHub's OAuth apps, for instance, revoke with
/// <c>DELETE /applications/{client_id}/token</c> under Basic client credentials.
/// </remarks>
public sealed class SparkOneShotOAuthClient
{
    /// <summary>The provider's authorization endpoint, e.g. <c>https://github.com/login/oauth/authorize</c>.</summary>
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    /// <summary>The provider's token endpoint, e.g. <c>https://github.com/login/oauth/access_token</c>.</summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>The OAuth client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The OAuth client secret, sent to the token endpoint and handed to <see cref="Revoke"/>.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Revokes a token when its lease is disposed. <see langword="null"/> means the provider has no
    /// revocation, and disposing the lease only forgets the token.
    /// </summary>
    public Func<SparkOneShotOAuthRevocation, CancellationToken, Task>? Revoke { get; set; }
}

/// <summary>What <see cref="SparkOneShotOAuthClient.Revoke"/> receives.</summary>
/// <param name="Http">An <see cref="HttpClient"/> from the factory, for the revocation call.</param>
/// <param name="Client">The client the token was issued to.</param>
/// <param name="AccessToken">The token to revoke.</param>
public sealed record SparkOneShotOAuthRevocation(HttpClient Http, SparkOneShotOAuthClient Client, string AccessToken);

/// <summary>The named <see cref="SparkOneShotOAuthClient"/>s.</summary>
public sealed class SparkOneShotOAuthOptions
{
    /// <summary>The clients by name. Names are case-sensitive.</summary>
    public Dictionary<string, SparkOneShotOAuthClient> Clients { get; } = new(StringComparer.Ordinal);
}
