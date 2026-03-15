using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Authorization.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class CsrfRefresh : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/csrf-refresh", (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<CsrfRefresh>();
            return endpoint.Handle();
        });
    }

    public IResult Handle() => Results.Ok();
}
