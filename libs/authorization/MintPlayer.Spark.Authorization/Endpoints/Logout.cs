using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization.Endpoints;

[MemberOf<SparkAuthGroup>]
internal sealed class Logout : IPostEndpoint
{
    public static string Path => "/logout";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Ok();
    }
}
