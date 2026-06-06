using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Culture;

internal sealed partial class GetCulture : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/culture";

    [Inject] private readonly ICultureLoader cultureLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var culture = cultureLoader.GetCulture();
        return Results.Json(culture);
    }
}
