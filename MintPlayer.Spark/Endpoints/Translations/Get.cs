using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Translations;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetTranslations
{
    [Inject] private readonly ITranslationsLoader translationsLoader;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var translations = translationsLoader.GetTranslations();
        await httpContext.Response.WriteAsJsonAsync(translations);
    }
}
