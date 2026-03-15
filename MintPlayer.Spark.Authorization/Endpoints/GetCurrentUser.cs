using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Authorization.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class GetCurrentUser : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/me", (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<GetCurrentUser>();
            return endpoint.Handle(context);
        });
    }

    public IResult Handle(HttpContext httpContext)
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
