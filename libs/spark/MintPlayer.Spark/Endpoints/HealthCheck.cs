using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Endpoints;

[MemberOf<SparkGroup>]
internal sealed class SparkHealthCheck : IGetEndpoint
{
    public static string Path => "/";

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        return Task.FromResult(Results.Text("Spark Middleware is active!"));
    }
}
