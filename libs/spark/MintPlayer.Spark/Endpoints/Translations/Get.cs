using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Endpoints.Translations;

[MemberOf<SparkGroup>]
internal sealed partial class GetTranslations : IGetEndpoint
{
    public static string Path => "/translations";

    // A read: server-side rendering may supply its response (SparkPrerenderableAttribute).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(SparkPrerenderableAttribute.Instance);

    [Inject] private readonly ITranslationsLoader translationsLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var translations = translationsLoader.GetAll();
        return Results.Json(translations);
    }
}
