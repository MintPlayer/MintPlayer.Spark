using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class DeleteLookupReferenceValue
{
    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext, string name, string key)
    {
        try
        {
            await lookupReferenceService.DeleteValueAsync(name, key);
            httpContext.Response.StatusCode = 204;
        }
        catch (InvalidOperationException ex)
        {
            httpContext.Response.StatusCode = 400;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
}
