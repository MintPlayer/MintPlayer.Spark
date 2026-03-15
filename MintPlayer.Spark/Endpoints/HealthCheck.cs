using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Endpoints;

internal sealed class SparkHealthCheck : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/";

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        return Task.FromResult(Results.Text("Spark Middleware is active!"));
    }
}
