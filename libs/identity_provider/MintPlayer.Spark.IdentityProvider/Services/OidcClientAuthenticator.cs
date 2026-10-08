using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>The outcome of authenticating a client at a back-channel endpoint.</summary>
/// <param name="Method">The method that succeeded: one of <see cref="OidcClientAuthMethods"/>.</param>
/// <param name="Certificate">The client's TLS certificate, when it presented one (mTLS authentication, RFC 8705 binding).</param>
internal sealed record OidcClientAuthentication(
    OidcApplication? Application,
    string? Method,
    X509Certificate2? Certificate,
    string? Error = null,
    string? ErrorDescription = null)
{
    public bool Succeeded => Application is not null && Error is null;

    /// <summary>The RFC 6749 §5.2 error response; 401 with <c>WWW-Authenticate</c> for <c>invalid_client</c>.</summary>
    public IResult ToResult(HttpContext http)
    {
        if (Error == "invalid_client")
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"token\"";
        return Results.Json(new { error = Error, error_description = ErrorDescription },
            statusCode: Error == "invalid_client" ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest);
    }
}

/// <summary>The token endpoint authentication methods (RFC 7591 §2, RFC 8705 §2).</summary>
public static class OidcClientAuthMethods
{
    public const string SecretBasic = "client_secret_basic";
    public const string SecretPost = "client_secret_post";
    public const string PrivateKeyJwt = "private_key_jwt";
    public const string TlsClientAuth = "tls_client_auth";
    public const string SelfSignedTlsClientAuth = "self_signed_tls_client_auth";
    public const string None = "none";

    public static readonly string[] All = [SecretBasic, SecretPost, PrivateKeyJwt, TlsClientAuth, SelfSignedTlsClientAuth, None];
}

/// <summary>
/// Authenticates the client at the token, introspection, revocation, PAR and device endpoints
/// (<c>docs/identity_provider_platform_PRD.md</c> D8): <c>client_secret_basic</c>, <c>client_secret_post</c>,
/// <c>private_key_jwt</c> (RFC 7523), <c>tls_client_auth</c> and <c>self_signed_tls_client_auth</c>
/// (RFC 8705), or none for a public client. One place, where there were three copies of a
/// <c>client_secret_post</c>-only check.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Presenting more than one method is refused (RFC 6749 §2.3: "MUST NOT use more than one").</item>
/// <item>A client registered with a <see cref="OidcApplication.TokenEndpointAuthMethod"/> may use only that one.</item>
/// <item><b>Uniform failure (O15):</b> an unknown client, a disabled one and a wrong secret all answer
/// <c>invalid_client</c>, and an unknown client still pays for one hash verification, so the response
/// time does not tell which client ids exist.</item>
/// <item>A secret's <c>LastUsedAt</c> is stamped at most once an hour (D5).</item>
/// <item><b>Failure throttle (D9):</b> after <see cref="SparkIdentityProviderRateLimitOptions.ClientAuthenticationFailures"/>
/// failures from one IP address for one existing client, that pair answers <c>invalid_client</c> without verifying
/// anything until the window ends. Keyed on the pair, not on the client alone, so nobody who merely knows a client id
/// can lock the real client out. The answer is the same <c>invalid_client</c> as every other failure, so the throttle
/// is no oracle.</item>
/// </list>
/// </remarks>
internal sealed class OidcClientAuthenticator(
    IDocumentStore store, OidcClientKeys clientKeys, OidcIssuer issuer, IMemoryCache cache, SparkIdentityProviderOptions options)
{
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>A hash nobody's secret verifies against, for the dummy check on an unknown client.</summary>
    private static readonly string DummyHash = ClientSecretHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public async Task<OidcClientAuthentication> AuthenticateAsync(
        HttpContext http, IFormCollection form, IAsyncDocumentSession session, CancellationToken ct)
    {
        // --- What did the client present? -----------------------------------------------------
        var (basicId, basicSecret) = ReadBasic(http.Request);
        var postSecret = form["client_secret"].FirstOrDefault();
        var assertionType = form["client_assertion_type"].FirstOrDefault();
        var assertion = form["client_assertion"].FirstOrDefault();
        var formClientId = form["client_id"].FirstOrDefault();

        var presented = new[] { basicId is not null, !string.IsNullOrEmpty(postSecret), !string.IsNullOrEmpty(assertion) }.Count(p => p);
        if (presented > 1)
            return Fail("invalid_request", "Use exactly one client authentication method.");

        string? clientId;
        JsonWebToken? assertionJwt = null;
        if (basicId is not null)
        {
            clientId = basicId;
            if (formClientId is not null && formClientId != basicId)
                return Fail("invalid_request", "client_id does not match the Authorization header.");
        }
        else if (!string.IsNullOrEmpty(assertion))
        {
            if (assertionType != JwtBearerAssertionType)
                return Fail("invalid_request", "Unsupported client_assertion_type.");
            try { assertionJwt = new JsonWebToken(assertion); }
            catch (ArgumentException) { return Fail("invalid_client", "Invalid client assertion."); }
            clientId = assertionJwt.Issuer;
            if (formClientId is not null && formClientId != clientId)
                return Fail("invalid_request", "client_id does not match the client assertion.");
        }
        else
        {
            clientId = formClientId;
        }

        if (string.IsNullOrEmpty(clientId))
            return Fail("invalid_client", "Client authentication failed.");

        var app = await OidcAuthorizationFlow.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app is null || !app.Enabled)
        {
            // O15: as long as a real secret check would take.
            ClientSecretHasher.Verify(basicSecret ?? postSecret ?? "", DummyHash);
            return Fail("invalid_client", "Client authentication failed.");
        }

        var throttleKey = $"spark-identity-provider:client-auth-failures:{app.ClientId}|{http.Connection.RemoteIpAddress}";
        var failureLimit = options.RateLimits.ClientAuthenticationFailures;
        if (failureLimit > 0 && cache.TryGetValue(throttleKey, out FailureCount? counted) && counted!.Value >= failureLimit)
            return Fail("invalid_client", "Client authentication failed.");

        var certificate = await http.Connection.GetClientCertificateAsync(ct);
        var method = basicId is not null ? OidcClientAuthMethods.SecretBasic
            : !string.IsNullOrEmpty(postSecret) ? OidcClientAuthMethods.SecretPost
            : assertionJwt is not null ? OidcClientAuthMethods.PrivateKeyJwt
            : app.TokenEndpointAuthMethod is OidcClientAuthMethods.TlsClientAuth or OidcClientAuthMethods.SelfSignedTlsClientAuth && certificate is not null
                ? app.TokenEndpointAuthMethod
                : OidcClientAuthMethods.None;

        if (!string.IsNullOrEmpty(app.TokenEndpointAuthMethod) && !MethodAllowed(app.TokenEndpointAuthMethod, method))
            return CountFailure(throttleKey);

        var ok = method switch
        {
            OidcClientAuthMethods.SecretBasic => await VerifySecretAsync(app, basicSecret ?? "", ct),
            OidcClientAuthMethods.SecretPost => await VerifySecretAsync(app, postSecret!, ct),
            OidcClientAuthMethods.PrivateKeyJwt => await VerifyAssertionAsync(app, assertion!, http, ct),
            OidcClientAuthMethods.TlsClientAuth => VerifySubjectDn(app, certificate),
            OidcClientAuthMethods.SelfSignedTlsClientAuth => await VerifySelfSignedAsync(app, certificate, ct),
            // Fail closed: only a client explicitly marked public, holding no secrets, may skip
            // authentication. Comparing == "confidential" once meant a stray case or space silently
            // disabled client authentication altogether.
            _ => string.Equals(app.ClientType, "public", StringComparison.OrdinalIgnoreCase) && app.Secrets.Count == 0,
        };

        return ok
            ? new OidcClientAuthentication(app, method, certificate)
            : CountFailure(throttleKey);
    }

    /// <summary>A counter that lives for one window from its first failure; a success does not reset it.</summary>
    private sealed class FailureCount { public int Value; }

    private OidcClientAuthentication CountFailure(string throttleKey)
    {
        var limits = options.RateLimits;
        if (limits.ClientAuthenticationFailures > 0)
        {
            var count = cache.GetOrCreate(throttleKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = limits.ClientAuthenticationFailureWindow;
                return new FailureCount();
            })!;
            Interlocked.Increment(ref count.Value);
        }
        return Fail("invalid_client", "Client authentication failed.");
    }

    /// <summary>A client registered for a secret method may use either secret method; the others are exact.</summary>
    private static bool MethodAllowed(string registered, string used)
        => registered == used
        || (registered is OidcClientAuthMethods.SecretBasic or OidcClientAuthMethods.SecretPost
            && used is OidcClientAuthMethods.SecretBasic or OidcClientAuthMethods.SecretPost);

    private static OidcClientAuthentication Fail(string error, string description) => new(null, null, null, error, description);

    /// <summary>RFC 6749 §2.3.1: both halves are form-url-encoded before being joined with a colon.</summary>
    private static (string? Id, string? Secret) ReadBasic(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
            return (null, null);

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter)); }
        catch (FormatException) { return ("", null); }

        var colon = decoded.IndexOf(':');
        if (colon < 0) return ("", null);
        return (Uri.UnescapeDataString(decoded[..colon].Replace('+', ' ')), Uri.UnescapeDataString(decoded[(colon + 1)..].Replace('+', ' ')));
    }

    /// <summary>
    /// Checks every unexpired secret even once one matches: short-circuiting on the first hit would
    /// leak, through timing, which of a rotating set was presented.
    /// </summary>
    private async Task<bool> VerifySecretAsync(OidcApplication app, string secret, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        ClientSecret? matched = null;
        foreach (var candidate in app.Secrets)
        {
            if (candidate.ExpiresAt != null && candidate.ExpiresAt <= now)
                continue;
            if (ClientSecretHasher.Verify(secret, candidate.Hash))
                matched ??= candidate;
        }

        if (matched is null && app.Secrets.Count == 0)
            ClientSecretHasher.Verify(secret, DummyHash); // O15: a client with no secrets costs the same

        if (matched is { SecretId: { } secretId } && (matched.LastUsedAt is null || matched.LastUsedAt < now.AddHours(-1)))
            await StampLastUsedAsync(app.Id!, secretId, now, ct);

        return matched is not null;
    }

    /// <summary>D5: when a secret was last used, at most once an hour, in a session of its own so a failing grant does not lose it.</summary>
    private async Task StampLastUsedAsync(string applicationId, string secretId, DateTime now, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        var app = await session.LoadAsync<OidcApplication>(applicationId, ct);
        if (app?.Secrets.FirstOrDefault(s => s.SecretId == secretId) is { } secret)
        {
            secret.LastUsedAt = now;
            await session.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// <c>private_key_jwt</c> (RFC 7523 §3): signed by one of the client's keys, <c>iss</c> = <c>sub</c> =
    /// the client id, <c>aud</c> naming this provider, unexpired, and its <c>jti</c> used once.
    /// </summary>
    private async Task<bool> VerifyAssertionAsync(OidcApplication app, string assertion, HttpContext http, CancellationToken ct)
    {
        var keys = await clientKeys.GetSigningKeysAsync(app, ct);
        if (keys.Count == 0)
            return false;

        var iss = issuer.Resolve(http.Request);
        var endpoint = $"{iss}{http.Request.Path}";
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = app.ClientId,
            ValidAudiences = [iss, endpoint, $"{iss}/connect/token"],
            IssuerSigningKeys = keys,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return false;
        if (jwt.Subject != app.ClientId || string.IsNullOrEmpty(jwt.Id))
            return false;

        // Replay: the jti is kept until the assertion would have expired anyway.
        return await OidcReplayCache.TryUseAsync(store, $"assertion:{app.ClientId}:{jwt.Id}", jwt.ValidTo, ct);
    }

    /// <summary><c>tls_client_auth</c> (RFC 8705 §2.1.1): a CA-issued certificate with the registered subject DN.</summary>
    private static bool VerifySubjectDn(OidcApplication app, X509Certificate2? certificate)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(app.TlsClientAuthSubjectDn))
            return false;
        var expected = new X500DistinguishedName(app.TlsClientAuthSubjectDn).Name;
        return string.Equals(certificate.SubjectName.Name, expected, StringComparison.OrdinalIgnoreCase)
            && certificate.NotAfter > DateTime.Now && certificate.NotBefore < DateTime.Now;
    }

    /// <summary><c>self_signed_tls_client_auth</c> (RFC 8705 §2.2): the certificate is one the client registered in its JWKS.</summary>
    private async Task<bool> VerifySelfSignedAsync(OidcApplication app, X509Certificate2? certificate, CancellationToken ct)
    {
        if (certificate is null || await clientKeys.GetKeySetAsync(app, ct) is not { } set)
            return false;
        var thumbprint = Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256));
        return set.Keys.Any(k =>
            string.Equals(k.X5tS256, thumbprint, StringComparison.Ordinal)
            || k.X5c.Any(c => CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(c), certificate.RawData)));
    }
}
