using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.ProgramUnits;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetProgramUnits
{
    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var programUnits = programUnitsLoader.GetProgramUnits();
        await httpContext.Response.WriteAsJsonAsync(programUnits);
    }
}
