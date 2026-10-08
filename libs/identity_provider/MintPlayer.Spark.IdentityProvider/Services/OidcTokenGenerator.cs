using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

internal class OidcTokenGenerator
{
    private readonly OidcSigningKeyService _signingKeyService;

    public OidcTokenGenerator(OidcSigningKeyService signingKeyService)
    {
        _signingKeyService = signingKeyService;
    }

    /// <summary>
    /// Generates an ID token with claims driven by OidcScope.ClaimTypes from the database.
    /// </summary>
    /// <param name="lifetimeMinutes">
    /// The id_token's own lifetime (<see cref="OidcApplication.IdTokenLifetimeMinutes"/>). It used to
    /// reuse the access token's: an id_token is consumed once, at sign-in, so it has no reason to stay
    /// valid as long as a credential that is presented on every API call.
    /// </param>
    /// <param name="accessToken">
    /// The access token issued in the same response, if any; its <c>at_hash</c> (OIDC Core §3.1.3.6)
    /// binds the two, so a relying party can tell the access token was not swapped.
    /// </param>
    /// <param name="authTime">
    /// When the user last actually authenticated (OIDC Core §2, <c>auth_time</c>) — the sign-in
    /// instant, not the token's issue time. Omitted when unknown.
    /// </param>
    public string GenerateIdToken(
        SparkUser user,
        OidcApplication app,
        string issuer,
        IReadOnlyList<OidcScope> grantedScopes,
        string? nonce,
        int lifetimeMinutes = 5,
        string? accessToken = null,
        DateTimeOffset? authTime = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id!),
        };

        // Typed values (email_verified is a JSON boolean, auth_time a number) go through the
        // descriptor's own dictionary, which serializes them as typed JSON. A Claim on the Subject is
        // a string unless the handler happens to honour its value type.
        var typedClaims = new Dictionary<string, object>();

        // Resolve claims from scope definitions
        foreach (var claim in ResolveUserClaims(user, grantedScopes))
        {
            if (claim.ValueType == ClaimValueTypes.Boolean)
                typedClaims[claim.Type] = bool.Parse(claim.Value);
            else
                claims.Add(claim);
        }

        if (!string.IsNullOrEmpty(nonce))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Nonce, nonce));
        }

        if (!string.IsNullOrEmpty(accessToken))
            typedClaims["at_hash"] = AccessTokenHash(accessToken);

        if (authTime is { } at)
            typedClaims["auth_time"] = at.ToUnixTimeSeconds();

        var key = _signingKeyService.GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Claims = typedClaims,
            Issuer = issuer,
            Audience = app.ClientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            SigningCredentials = credentials,
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    /// <summary>
    /// <c>at_hash</c> for an RS256-signed id_token: the base64url of the left-most half of the SHA-256
    /// of the access token's ASCII octets (OIDC Core §3.1.3.6).
    /// </summary>
    internal static string AccessTokenHash(string accessToken)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash, 0, hash.Length / 2);
    }

    /// <summary>
    /// Generates an access token with scope and audience claims driven by the database.
    /// Client claims (OidcApplication.Claims) are included.
    /// user may be null for client_credentials grant.
    /// <para>
    /// Returns the <c>jti</c> alongside the token: the caller must store the governing
    /// <see cref="OidcToken"/> under <see cref="OidcTokenReference.DocumentId"/> of it, or the
    /// token can never be revoked (see <see cref="AccessTokens"/>).
    /// </para>
    /// </summary>
    public (string Token, string Jti) GenerateAccessToken(
        SparkUser? user,
        OidcApplication app,
        string issuer,
        IReadOnlyList<OidcScope> grantedScopes,
        int lifetimeMinutes = 60)
    {
        var scopeNames = grantedScopes.Select(s => s.Name).ToList();
        var jti = OidcTokenReference.GenerateValue();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, jti),
            new("client_id", app.ClientId),
            new("scope", string.Join(" ", scopeNames)),
        };

        if (user != null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, user.Id!));
        }

        // Application claims carry the *client's own* authority, so they belong only in a
        // machine token — one issued to the client acting as itself, with no user behind it.
        //
        // They are emitted under their declared type, unprefixed: they were previously
        // namespaced as "client_{Type}", which silently defeated authorization, because Spark
        // resolves group membership purely from "group"/"groups"/role claims
        // (ClaimsGroupMembershipProvider). An application configured with {Type:"group"}
        // emitted "client_group", matched nothing, and every machine token authorized as a
        // member of no group at all.
        //
        // Restricting them to client_credentials is what makes dropping the prefix safe. On a
        // delegated grant the token speaks for the *user*, and merging the client's claims in
        // would hand every user who signs in through a service client that client's groups —
        // consent alone would make an end user an Administrator.
        if (user == null)
        {
            foreach (var cc in app.Claims)
            {
                claims.Add(new Claim(cc.Type, cc.Value));
            }
        }

        // Determine audience from scope definitions
        var audiences = grantedScopes
            .SelectMany(s => s.Audiences)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var key = _signingKeyService.GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            SigningCredentials = credentials,
        };

        // Every audience through the descriptor's own list. The extra ones used to travel as "aud"
        // claims on the Subject beside `Audience = audiences[0]`, and JsonWebTokenHandler emitted
        // only the descriptor's audience — so every audience but the first was dropped (measured by
        // OidcTokenGeneratorTests; a previous fix here moved the claims before the ClaimsIdentity
        // was built, which was not the cause). It narrowed rather than widened, so it failed
        // closed: a resource server named by a second scope simply rejected the token.
        // Scope-defined audiences win; a client with none falls back to itself.
        foreach (var audience in audiences.Count > 0 ? audiences : [app.ClientId])
            descriptor.Audiences.Add(audience);

        var handler = new JsonWebTokenHandler();
        return (handler.CreateToken(descriptor), jti);
    }

    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Resolves user claim values from the granted scopes' ClaimTypes.
    /// Maps well-known claim types to SparkUser properties.
    /// </summary>
    internal static List<Claim> ResolveUserClaims(SparkUser user, IReadOnlyList<OidcScope> grantedScopes)
    {
        var claims = new List<Claim>();
        var requestedClaimTypes = grantedScopes
            .SelectMany(s => s.ClaimTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // "sub" is always handled separately (added by the caller)
        requestedClaimTypes.Remove("sub");

        foreach (var claimType in requestedClaimTypes)
        {
            switch (claimType.ToLowerInvariant())
            {
                case "name":
                    if (!string.IsNullOrEmpty(user.UserName))
                        claims.Add(new Claim("name", user.UserName));
                    break;
                case "email":
                    if (!string.IsNullOrEmpty(user.Email))
                        claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
                    break;
                case "email_verified":
                    // A JSON boolean, as in userinfo (OIDC Core §5.1). It was the string "true" here
                    // and a boolean there; GenerateIdToken writes a Boolean-typed claim as a boolean.
                    if (!string.IsNullOrEmpty(user.Email))
                        claims.Add(new Claim("email_verified", user.EmailConfirmed ? "true" : "false", ClaimValueTypes.Boolean));
                    break;
                case "role":
                    foreach (var role in user.Roles)
                        claims.Add(new Claim("role", role));
                    break;
                case "preferred_username":
                    if (!string.IsNullOrEmpty(user.UserName))
                        claims.Add(new Claim("preferred_username", user.UserName));
                    break;
                case "given_name":
                case "family_name":
                    if (StoredClaim(user, claimType) is { } stored)
                        claims.Add(new Claim(claimType.ToLowerInvariant(), stored));
                    break;
                default:
                    // Other standard claims (picture, locale, ...) have no source on SparkUser.
                    break;
            }
        }

        return claims;
    }

    /// <summary>
    /// A name part from the user's stored claims (<see cref="SparkUser.Claims"/>), under its OIDC name or
    /// the matching <see cref="ClaimTypes"/> URI, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="SparkUser"/> has no given or family name of its own, so these exist only where an
    /// application stores them as user claims (for example from an external provider's ticket). A
    /// user without them simply gets no such claim — never an empty one.
    /// </remarks>
    internal static string? StoredClaim(SparkUser user, string oidcClaimType)
    {
        var uri = oidcClaimType.ToLowerInvariant() switch
        {
            "given_name" => ClaimTypes.GivenName,
            "family_name" => ClaimTypes.Surname,
            _ => null,
        };

        var value = user.Claims.FirstOrDefault(c =>
                string.Equals(c.ClaimType, oidcClaimType, StringComparison.OrdinalIgnoreCase)
                || (uri is not null && string.Equals(c.ClaimType, uri, StringComparison.Ordinal)))
            ?.ClaimValue;

        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Resolves user claim values for the UserInfo endpoint.
    /// Returns a dictionary for JSON serialization.
    /// </summary>
    internal static Dictionary<string, object> ResolveUserInfoClaims(SparkUser user, IReadOnlyList<OidcScope> grantedScopes)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id!,
        };

        var requestedClaimTypes = grantedScopes
            .SelectMany(s => s.ClaimTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        requestedClaimTypes.Remove("sub");

        foreach (var claimType in requestedClaimTypes)
        {
            switch (claimType.ToLowerInvariant())
            {
                case "name":
                    if (!string.IsNullOrEmpty(user.UserName))
                        claims["name"] = user.UserName;
                    break;
                case "email":
                    if (!string.IsNullOrEmpty(user.Email))
                        claims["email"] = user.Email;
                    break;
                case "email_verified":
                    if (!string.IsNullOrEmpty(user.Email))
                        claims["email_verified"] = user.EmailConfirmed;
                    break;
                case "role":
                    // "role", as in the id_token; this said "roles", so a relying party mapping one
                    // name saw roles from only one of its two sources.
                    if (user.Roles.Count > 0)
                        claims["role"] = user.Roles;
                    break;
                case "preferred_username":
                    if (!string.IsNullOrEmpty(user.UserName))
                        claims["preferred_username"] = user.UserName;
                    break;
                case "given_name":
                case "family_name":
                    if (StoredClaim(user, claimType) is { } stored)
                        claims[claimType.ToLowerInvariant()] = stored;
                    break;
                default:
                    break;
            }
        }

        return claims;
    }
}
