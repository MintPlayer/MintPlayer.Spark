using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// GET  /spark/auth/logins — list current user's external logins.
/// DELETE /spark/auth/logins/{provider} — remove an external login.
/// </summary>
internal static class ExternalLogins
{
    public static async Task<IResult> HandleList(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var (userManager, user) = await ResolveUserAsync(httpContext, userId, ct);
        if (user == null)
        {
            return Results.Unauthorized();
        }

        var getLoginsMethod = userManager.GetType().GetMethod("GetLoginsAsync")!;
        var logins = await (dynamic)getLoginsMethod.Invoke(userManager, [user])!;

        var result = ((IList<UserLoginInfo>)logins)
            .Select(l => new
            {
                loginProvider = l.LoginProvider,
                providerDisplayName = l.ProviderDisplayName,
                providerKey = l.ProviderKey,
            })
            .ToArray();

        return Results.Ok(result);
    }

    public static async Task<IResult> HandleRemove(HttpContext httpContext, string provider)
    {
        var ct = httpContext.RequestAborted;

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var (userManager, user) = await ResolveUserAsync(httpContext, userId, ct);
        if (user == null)
        {
            return Results.Unauthorized();
        }

        // Find the login entry for this provider
        var getLoginsMethod = userManager.GetType().GetMethod("GetLoginsAsync")!;
        var logins = await (dynamic)getLoginsMethod.Invoke(userManager, [user])!;

        var login = ((IList<UserLoginInfo>)logins)
            .FirstOrDefault(l => string.Equals(l.LoginProvider, provider, StringComparison.OrdinalIgnoreCase));

        if (login == null)
        {
            return Results.NotFound(new { error = $"No external login found for provider '{provider}'." });
        }

        var removeLoginMethod = userManager.GetType().GetMethod("RemoveLoginAsync")!;
        var result = await (dynamic)removeLoginMethod.Invoke(userManager, [user, login.LoginProvider, login.ProviderKey])!;

        if (!((IdentityResult)result).Succeeded)
        {
            return Results.Problem("Failed to remove external login.");
        }

        return Results.Ok();
    }

    private static async Task<(object userManager, object? user)> ResolveUserAsync(
        HttpContext httpContext, string userId, CancellationToken ct)
    {
        var sparkRegistry = httpContext.RequestServices.GetRequiredService<SparkModuleRegistry>();
        var userType = sparkRegistry.IdentityUserType
            ?? throw new InvalidOperationException("No identity user type configured.");

        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var userManager = httpContext.RequestServices.GetRequiredService(userManagerType);

        var findByIdMethod = userManagerType.GetMethod("FindByIdAsync")!;
        var user = await (dynamic)findByIdMethod.Invoke(userManager, [userId])!;

        return (userManager, (object?)user);
    }
}
