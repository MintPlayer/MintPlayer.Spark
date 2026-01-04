using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class ListLookupReferences
{
    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var lookupReferences = await lookupReferenceService.GetAllAsync();
        await httpContext.Response.WriteAsJsonAsync(lookupReferences);
    }
}
