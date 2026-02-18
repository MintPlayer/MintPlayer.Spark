using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal static class GetCurrentUser
{
    public static IResult Handle(HttpContext httpContext)
    {
        var identity = httpContext.User.Identity;

        if (identity is null || !identity.IsAuthenticated)
        {
            return Results.Ok(new { isAuthenticated = false });
        }

        var userName = httpContext.User.FindFirstValue(ClaimTypes.Name);
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var roles = httpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        return Results.Ok(new
        {
            isAuthenticated = true,
            userName,
            email,
            roles,
        });
    }
}
