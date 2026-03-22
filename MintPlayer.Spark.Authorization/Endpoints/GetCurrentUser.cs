using MintPlayer.AspNetCore.Endpoints;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal sealed class GetCurrentUser : IGetEndpoint, IMemberOf<SparkAuthGroup>
{
    public static string Path => "/me";

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var identity = httpContext.User.Identity;

        if (identity is null || !identity.IsAuthenticated)
        {
            return Task.FromResult(Results.Ok(new { isAuthenticated = false }));
        }

        var userName = httpContext.User.FindFirstValue(ClaimTypes.Name);
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var roles = httpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        return Task.FromResult(Results.Ok(new
        {
            isAuthenticated = true,
            userName,
            email,
            roles,
        }));
    }
}
