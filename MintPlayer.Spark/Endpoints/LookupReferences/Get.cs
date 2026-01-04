using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class GetLookupReference
{
    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext, string name)
    {
        var lookupReference = await lookupReferenceService.GetAsync(name);

        if (lookupReference == null)
        {
            httpContext.Response.StatusCode = 404;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"LookupReference '{name}' not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(lookupReference);
    }
}
