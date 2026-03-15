using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Translations;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetTranslations : IEndpoint
{
    [Inject] private readonly ITranslationsLoader translationsLoader;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/translations", async (HttpContext context, GetTranslations action) =>
            await action.HandleAsync(context));
    }

    public async Task HandleAsync(HttpContext httpContext)
    {
        var translations = translationsLoader.GetTranslations();
        await httpContext.Response.WriteAsJsonAsync(translations);
    }
}
