using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// A client's public keys (<c>docs/identity_provider_platform_PRD.md</c> D8): from its registered
/// <see cref="OidcApplication.Jwks"/>, or fetched from its <see cref="OidcApplication.JwksUri"/> and
/// cached for five minutes. Used to verify <c>private_key_jwt</c> client assertions and signed
/// request objects (JAR), and to encrypt id_tokens and userinfo responses for the client.
/// </summary>
internal sealed class OidcClientKeys(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<OidcClientKeys> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>The client's JWKS, or null when it registered none or it cannot be read.</summary>
    public async Task<JsonWebKeySet?> GetKeySetAsync(OidcApplication app, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(app.Jwks))
            return Parse(app.Jwks, app.ClientId);

        if (string.IsNullOrWhiteSpace(app.JwksUri) || !Uri.TryCreate(app.JwksUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var key = "oidc-client-jwks:" + uri;
        if (cache.TryGetValue(key, out JsonWebKeySet? cached))
            return cached;

        try
        {
            using var client = httpClientFactory.CreateClient("Spark.IdentityProvider.ClientJwks");
            client.Timeout = TimeSpan.FromSeconds(10);
            var json = await client.GetStringAsync(uri, ct);
            var set = Parse(json, app.ClientId);
            if (set is not null)
                cache.Set(key, set, CacheFor);
            return set;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch the JWKS of client {ClientId} from {Uri}.", app.ClientId, uri);
            return null;
        }
    }

    /// <summary>The client's signature-verification keys.</summary>
    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(OidcApplication app, CancellationToken ct)
        => (await GetKeySetAsync(app, ct))?.Keys.Where(k => k.Use is null or "sig").Cast<SecurityKey>().ToList() ?? [];

    /// <summary>The client's encryption key for <paramref name="algorithm"/> (e.g. <c>RSA-OAEP</c>), or null.</summary>
    public async Task<JsonWebKey?> GetEncryptionKeyAsync(OidcApplication app, string algorithm, CancellationToken ct)
        => (await GetKeySetAsync(app, ct))?.Keys.FirstOrDefault(k =>
            k.Use is null or "enc" && (k.Alg is null || string.Equals(k.Alg, algorithm, StringComparison.Ordinal)));

    private JsonWebKeySet? Parse(string json, string clientId)
    {
        try
        {
            return new JsonWebKeySet(json) { SkipUnresolvedJsonWebKeys = true };
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "The JWKS registered for client {ClientId} is not a valid JWK set.", clientId);
            return null;
        }
    }
}
