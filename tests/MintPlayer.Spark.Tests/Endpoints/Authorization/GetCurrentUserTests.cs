using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Endpoints;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Endpoints.Authorization;

/// <summary>
/// The anonymous branch of <c>GET /spark/auth/me</c>, which answers before the user store is asked.
/// </summary>
/// <remarks>
/// The authenticated answer is read through <c>UserManager</c> now, not from the cookie's claims, so
/// it is tested against a real store: <c>AccountFlowTests.Me_reflects_the_stored_user_name_…</c>. The
/// claim-reading cases that used to live here pinned exactly the staleness that was fixed.
/// </remarks>
public class GetCurrentUserTests
{
    // The anonymous branch never touches the store, so no UserManager is needed to reach it.
    private static GetCurrentUser<SparkUser> Endpoint() => new(null!);

    [Fact]
    public async Task Returns_isAuthenticated_false_when_identity_is_missing()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal() };

        var body = await ExecuteResultAsync(await Endpoint().HandleAsync(context), context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
        doc.RootElement.TryGetProperty("userName", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Returns_isAuthenticated_false_when_identity_is_not_authenticated()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        var body = await ExecuteResultAsync(await Endpoint().HandleAsync(context), context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }

    private static async Task<string> ExecuteResultAsync(IResult result, HttpContext context)
    {
        var stream = new MemoryStream();
        context.Response.Body = stream;
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        await result.ExecuteAsync(context);
        stream.Position = 0;
        return await new StreamReader(stream).ReadToEndAsync();
    }
}
