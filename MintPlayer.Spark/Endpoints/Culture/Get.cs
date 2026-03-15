using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Culture;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetCulture : IEndpoint
{
    [Inject] private readonly ICultureLoader cultureLoader;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/culture", async (HttpContext context, GetCulture action) =>
            await action.HandleAsync(context));
    }

    public async Task HandleAsync(HttpContext httpContext)
    {
        var culture = cultureLoader.GetCulture();
        await httpContext.Response.WriteAsJsonAsync(culture);
    }
}
