using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal static class Logout
{
    public static async Task<IResult> Handle(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Ok();
    }
}
