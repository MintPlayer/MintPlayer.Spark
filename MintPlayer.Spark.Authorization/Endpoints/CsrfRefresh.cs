using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal sealed class CsrfRefresh : IPostEndpoint, IMemberOf<SparkAuthGroup>
{
    public static string Path => "/csrf-refresh";

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        return Task.FromResult(Results.Ok());
    }
}
