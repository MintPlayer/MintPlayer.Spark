using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Culture;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetCulture
{
    [Inject] private readonly ICultureLoader cultureLoader;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var culture = cultureLoader.GetCulture();
        await httpContext.Response.WriteAsJsonAsync(culture);
    }
}
