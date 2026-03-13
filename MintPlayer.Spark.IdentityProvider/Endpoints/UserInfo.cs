using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class UserInfo
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;

        // Extract Bearer token from Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            return;
        }

        var accessToken = authHeader["Bearer ".Length..];

        // Validate the access token JWT
        var signingKeyService = context.RequestServices.GetRequiredService<OidcSigningKeyService>();
        var issuer = $"{context.Request.Scheme}://{context.Request.Host}";

        var handler = new JsonWebTokenHandler();
        var validationResult = await handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = signingKeyService.GetSigningKey(),
        });

        if (!validationResult.IsValid)
        {
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            return;
        }

        validationResult.Claims.TryGetValue("sub", out var subObj);
        var subject = subObj?.ToString();
        validationResult.Claims.TryGetValue("scope", out var scopeObj);
        var scopeString = scopeObj?.ToString() ?? "";

        if (string.IsNullOrEmpty(subject))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token", error_description = "Missing subject claim." });
            return;
        }

        // Load user
        var registry = context.RequestServices.GetRequiredService<SparkModuleRegistry>();
        var userType = registry.IdentityUserType ?? typeof(SparkUser);
        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var userManager = context.RequestServices.GetRequiredService(userManagerType);

        var findByIdMethod = userManagerType.GetMethod("FindByIdAsync")!;
        var user = await (dynamic)findByIdMethod.Invoke(userManager, [subject])! as SparkUser;

        if (user == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token", error_description = "User not found." });
            return;
        }

        // Build response based on granted scopes
        var scopes = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id!,
        };

        if (scopes.Contains("profile"))
        {
            if (!string.IsNullOrEmpty(user.UserName))
                claims["name"] = user.UserName;
        }

        if (scopes.Contains("email"))
        {
            if (!string.IsNullOrEmpty(user.Email))
            {
                claims["email"] = user.Email;
                claims["email_verified"] = user.EmailConfirmed;
            }
        }

        if (scopes.Contains("roles"))
        {
            claims["roles"] = user.Roles;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(claims);
    }
}
