using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

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
        var issuer = OidcIssuer.Resolve(context);

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        // Signature and expiry alone cannot tell that a token was revoked, so this endpoint
        // went on serving a revoked token's claims for the rest of its lifetime.
        var resolved = await AccessTokens.ResolveAsync(session, signingKeyService, accessToken, issuer, ct);

        if (resolved is not { IsActive: true })
        {
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            return;
        }

        var subject = resolved.Subject;
        var scopeString = resolved.Scope ?? "";

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

        // Load scope definitions from DB to resolve claims
        var scopeNames = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var grantedScopes = await session
            .Query<OidcScope>()
            .Where(s => s.Name.In(scopeNames) && s.Enabled)
            .ToListAsync(ct);

        // Resolve claims from scope definitions
        var claims = OidcTokenGenerator.ResolveUserInfoClaims(user, grantedScopes);

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(claims);
    }
}
