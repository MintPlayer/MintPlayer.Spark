using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Authorization.Endpoints;

namespace MintPlayer.Spark.Tests.Endpoints.Authorization;

public class GetCurrentUserTests
{
    [Fact]
    public async Task Returns_isAuthenticated_false_when_identity_is_missing()
    {
        var endpoint = new GetCurrentUser();
        var context = new DefaultHttpContext();
        // Bare ClaimsPrincipal — no identity — IsAuthenticated == false
        context.User = new ClaimsPrincipal();

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteResultAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
        doc.RootElement.TryGetProperty("userName", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Returns_isAuthenticated_false_when_identity_is_not_authenticated()
    {
        var endpoint = new GetCurrentUser();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()), // empty identity, not authenticated
        };

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteResultAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Returns_userName_email_and_roles_when_authenticated()
    {
        var endpoint = new GetCurrentUser();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "alice"),
                    new Claim(ClaimTypes.Email, "alice@example.com"),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim(ClaimTypes.Role, "Editor"),
                ],
                authenticationType: "TestScheme"
            )),
        };

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteResultAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("userName").GetString().Should().Be("alice");
        doc.RootElement.GetProperty("email").GetString().Should().Be("alice@example.com");
        doc.RootElement.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString()).Should().BeEquivalentTo(["Admin", "Editor"]);
    }

    [Fact]
    public async Task Returns_empty_roles_array_when_user_has_no_role_claims()
    {
        var endpoint = new GetCurrentUser();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "bob")],
                authenticationType: "TestScheme"
            )),
        };

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteResultAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("userName").GetString().Should().Be("bob");
        doc.RootElement.GetProperty("email").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("roles").GetArrayLength().Should().Be(0);
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
