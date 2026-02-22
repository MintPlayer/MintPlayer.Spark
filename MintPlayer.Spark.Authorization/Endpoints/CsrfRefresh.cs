using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.Authorization.Endpoints;

internal static class CsrfRefresh
{
    public static IResult Handle() => Results.Ok();
}
