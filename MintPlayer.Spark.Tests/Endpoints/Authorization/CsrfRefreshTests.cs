using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MintPlayer.Spark.Authorization.Endpoints;

namespace MintPlayer.Spark.Tests.Endpoints.Authorization;

public class CsrfRefreshTests
{
    [Fact]
    public async Task Returns_an_OK_IResult()
    {
        var endpoint = new CsrfRefresh();
        var context = new DefaultHttpContext();

        var result = await endpoint.HandleAsync(context);

        // Results.Ok() without a body is Microsoft.AspNetCore.Http.HttpResults.Ok
        // whose ExecuteAsync writes status 200 and an empty body. This endpoint's
        // job is to round-trip an authenticated request so the antiforgery
        // middleware emits a fresh XSRF-TOKEN cookie — the handler itself is a no-op.
        result.Should().BeOfType<Ok>();
    }
}
