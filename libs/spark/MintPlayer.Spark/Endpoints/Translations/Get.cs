using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Translations;

internal sealed partial class GetTranslations : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/translations";

    [Inject] private readonly ITranslationsLoader translationsLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var translations = translationsLoader.GetAll();
        return Results.Json(translations);
    }
}
