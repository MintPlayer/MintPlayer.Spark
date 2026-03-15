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
    public string GenerateIdToken(
        SparkUser user,
        OidcApplication app,
        string issuer,
        IReadOnlyList<OidcScope> grantedScopes,
        string? nonce,
        int lifetimeMinutes = 60)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id!),
        };

        // Resolve claims from scope definitions
        var resolvedClaims = ResolveUserClaims(user, grantedScopes);
        claims.AddRange(resolvedClaims);

        if (!string.IsNullOrEmpty(nonce))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Nonce, nonce));
        }

        var key = _signingKeyService.GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
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
    /// Generates an access token with scope and audience claims driven by the database.
    /// Client claims (OidcApplication.Claims) are included.
    /// user may be null for client_credentials grant.
    /// </summary>
    public string GenerateAccessToken(
        SparkUser? user,
        OidcApplication app,
        string issuer,
        IReadOnlyList<OidcScope> grantedScopes,
        int lifetimeMinutes = 60)
    {
        var scopeNames = grantedScopes.Select(s => s.Name).ToList();

        var claims = new List<Claim>
        {
            new("client_id", app.ClientId),
            new("scope", string.Join(" ", scopeNames)),
        };

        if (user != null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, user.Id!));
        }

        // Add client claims
        foreach (var cc in app.Claims)
        {
            claims.Add(new Claim($"client_{cc.Type}", cc.Value));
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

        // Set audience: use scope-defined audiences, or fall back to clientId
        if (audiences.Count == 1)
        {
            descriptor.Audience = audiences[0];
        }
        else if (audiences.Count > 1)
        {
            // Multiple audiences: add as individual claims since Audiences property is read-only
            descriptor.Audience = audiences[0];
            foreach (var aud in audiences.Skip(1))
            {
                claims.Add(new Claim(JwtRegisteredClaimNames.Aud, aud));
            }
        }
        else
        {
            descriptor.Audience = app.ClientId;
        }

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
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
                    if (!string.IsNullOrEmpty(user.Email))
                        claims.Add(new Claim("email_verified", user.EmailConfirmed.ToString().ToLowerInvariant()));
                    break;
                case "role":
                    foreach (var role in user.Roles)
                        claims.Add(new Claim("role", role));
                    break;
                case "preferred_username":
                    if (!string.IsNullOrEmpty(user.UserName))
                        claims.Add(new Claim("preferred_username", user.UserName));
                    break;
                default:
                    // For claim types not mapped to SparkUser properties (family_name, given_name,
                    // picture, locale, etc.), these would need to come from Identity user claims.
                    // A future enhancement can load them via UserManager.GetClaimsAsync().
                    break;
            }
        }

        return claims;
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
                    if (user.Roles.Count > 0)
                        claims["roles"] = user.Roles;
                    break;
                case "preferred_username":
                    if (!string.IsNullOrEmpty(user.UserName))
                        claims["preferred_username"] = user.UserName;
                    break;
                default:
                    break;
            }
        }

        return claims;
    }
}
