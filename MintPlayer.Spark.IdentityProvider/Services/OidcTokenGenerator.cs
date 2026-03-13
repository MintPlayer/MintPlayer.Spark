using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Services;

internal class OidcTokenGenerator
{
    private readonly OidcSigningKeyService _signingKeyService;

    public OidcTokenGenerator(OidcSigningKeyService signingKeyService)
    {
        _signingKeyService = signingKeyService;
    }

    public string GenerateIdToken(
        SparkUser user,
        string clientId,
        string issuer,
        IEnumerable<string> scopes,
        string? nonce,
        int lifetimeMinutes = 60)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id!),
        };

        var scopeSet = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (scopeSet.Contains("profile"))
        {
            if (!string.IsNullOrEmpty(user.UserName))
                claims.Add(new Claim("name", user.UserName));
        }

        if (scopeSet.Contains("email"))
        {
            if (!string.IsNullOrEmpty(user.Email))
            {
                claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
                claims.Add(new Claim("email_verified", user.EmailConfirmed.ToString().ToLowerInvariant()));
            }
        }

        if (scopeSet.Contains("roles"))
        {
            foreach (var role in user.Roles)
            {
                claims.Add(new Claim("role", role));
            }
        }

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
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            SigningCredentials = credentials,
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public string GenerateAccessToken(
        SparkUser user,
        string clientId,
        string issuer,
        IEnumerable<string> scopes,
        int lifetimeMinutes = 60)
    {
        var scopeList = scopes.ToList();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id!),
            new("client_id", clientId),
            new("scope", string.Join(" ", scopeList)),
        };

        var key = _signingKeyService.GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            SigningCredentials = credentials,
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
