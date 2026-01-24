using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped)]
public sealed partial class UpdateLookupReferenceValue
{
    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext, string name, string key)
    {
        try
        {
            var value = await httpContext.Request.ReadFromJsonAsync<LookupReferenceValueDto>();

            if (value == null)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
                return;
            }

            var result = await lookupReferenceService.UpdateValueAsync(name, key, value);
            await httpContext.Response.WriteAsJsonAsync(result);
        }
        catch (InvalidOperationException ex)
        {
            httpContext.Response.StatusCode = 400;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
}
