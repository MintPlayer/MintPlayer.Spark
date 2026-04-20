using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Endpoints;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Authorization;

public class LogoutTests
{
    [Fact]
    public async Task Calls_SignOutAsync_with_the_IdentityConstants_ApplicationScheme()
    {
        var authService = Substitute.For<IAuthenticationService>();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(authService)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };

        var endpoint = new Logout();
        var result = await endpoint.HandleAsync(context);

        await authService.Received(1).SignOutAsync(
            context,
            IdentityConstants.ApplicationScheme,
            properties: null);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Configure_marks_the_endpoint_with_RequireAntiforgeryTokenAttribute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        var app = builder.Build();

        var routeBuilder = app.MapPost("/test-logout", () => Results.Ok());
        InvokeConfigure<Logout>(routeBuilder);

        // Start the app so endpoint conventions materialize into the data source.
        await app.StartAsync();
        try
        {
            var endpoints = app.Services
                .GetRequiredService<IEnumerable<EndpointDataSource>>()
                .SelectMany(ds => ds.Endpoints);

            var hasAntiforgeryMetadata = endpoints.Any(
                e => e.Metadata.GetMetadata<RequireAntiforgeryTokenAttribute>() is not null);
            hasAntiforgeryMetadata.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static void InvokeConfigure<TEndpoint>(RouteHandlerBuilder builder)
        where TEndpoint : IEndpointBase
        => TEndpoint.Configure(builder);
}
