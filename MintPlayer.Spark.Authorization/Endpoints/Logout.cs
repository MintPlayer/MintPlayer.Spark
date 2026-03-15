using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Authorization.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class Logout : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/logout", async (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<Logout>();
            return await endpoint.Handle(context);
        }).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    public async Task<IResult> Handle(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Ok();
    }
}
