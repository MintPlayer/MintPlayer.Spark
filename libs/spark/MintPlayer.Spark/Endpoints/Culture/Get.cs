using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Endpoints.Culture;

[MemberOf<SparkGroup>]
internal sealed partial class GetCulture : IGetEndpoint
{
    public static string Path => "/culture";

    // A read: server-side rendering may supply its response (SparkPrerenderableAttribute).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(SparkPrerenderableAttribute.Instance);

    [Inject] private readonly ICultureLoader cultureLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var culture = cultureLoader.GetCulture();
        return Results.Json(culture);
    }
}
