using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Controllers;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// #300/#301 — <c>spark.AddControllers()</c> / <c>spark.UseControllers()</c>, the supported way to
/// mount an application's own controllers inside Spark's pipeline instead of beside it.
/// <para>
/// Asserted over <see cref="EndpointDataSource"/> rather than over status codes, for the same reason
/// <c>LocalCredentialModeTests</c> is: the claim is about what is in the route table, and a status
/// code would pass equally against shadowing middleware.
/// </para>
/// </summary>
public class SparkControllersExtensionsTests
{
    /// <summary>
    /// A bare <see cref="ISparkBuilder"/>. <c>AddSpark</c> itself would require RavenDB and the whole
    /// model pipeline for a decision that is two service registrations and one endpoint action.
    /// </summary>
    private sealed class TestSparkBuilder(IServiceCollection services) : ISparkBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    private static async Task<IHost> StartAsync(Action<ISparkBuilder> configure, bool alsoMapControllers = false)
    {
        SparkModuleRegistry? registry = null;

        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    var builder = new TestSparkBuilder(services);
                    registry = builder.Registry;
                    services.AddRouting();
                    configure(builder);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // Stands in for MapSpark(), which is what runs the registry's endpoint
                        // actions in a real app.
                        registry!.MapEndpoints(endpoints);

                        if (alsoMapControllers)
                            endpoints.MapControllers();
                    });
                }))
            .StartAsync();
    }

    private static IReadOnlyList<string> RoutesOf(IHost host) =>
        [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public async Task UseControllers_mounts_every_action()
    {
        using var host = await StartAsync(spark => spark
            .AddControllers(mvc => mvc.AddApplicationPart(typeof(SparkControllersExtensionsTests).Assembly))
            .UseControllers());

        RoutesOf(host).Should().Contain("api/spark-controllers-probe/ping");
    }

    [Fact]
    public async Task AddControllers_alone_mounts_nothing()
    {
        // The Add/Use split is ASP.NET Core's own, and it is worth keeping: an app may want MVC
        // configured without exposing anything through Spark's routes.
        using var host = await StartAsync(spark => spark
            .AddControllers(mvc => mvc.AddApplicationPart(typeof(SparkControllersExtensionsTests).Assembly)));

        RoutesOf(host).Should().NotContain("api/spark-controllers-probe/ping");
    }

    [Fact]
    public async Task UseControllers_called_twice_yields_one_endpoint_per_action()
    {
        using var host = await StartAsync(spark => spark
            .AddControllers(mvc => mvc.AddApplicationPart(typeof(SparkControllersExtensionsTests).Assembly))
            .UseControllers()
            .UseControllers());

        RoutesOf(host).Count(r => r == "api/spark-controllers-probe/ping").Should().Be(1);
    }

    [Fact]
    public async Task An_apps_own_MapControllers_alongside_ours_does_not_duplicate_the_route()
    {
        // The migration shape: an app adopts spark.UseControllers() and its old MapControllers() is
        // still there. MVC keeps a single ControllerActionEndpointDataSource per builder, so the
        // second call adds nothing — which is why SPARK010 is a warning about lost protection rather
        // than an error about a broken route table.
        using var host = await StartAsync(
            spark => spark
                .AddControllers(mvc => mvc.AddApplicationPart(typeof(SparkControllersExtensionsTests).Assembly))
                .UseControllers(),
            alsoMapControllers: true);

        RoutesOf(host).Count(r => r == "api/spark-controllers-probe/ping").Should().Be(1);
    }

    [Fact]
    public async Task An_apps_earlier_AddControllers_configuration_survives()
    {
        // Every demo calls builder.Services.AddControllers() before AddSpark. MVC's registration is
        // idempotent and returns a builder over the same options, so an earlier .AddJsonOptions(…)
        // must not be discarded by ours.
        var configured = false;

        using var host = await StartAsync(spark =>
        {
            spark.Services
                .AddControllers(mvc => mvc.MaxModelBindingCollectionSize = 42)
                .AddApplicationPart(typeof(SparkControllersExtensionsTests).Assembly);

            spark.AddControllers().UseControllers();
            configured = true;
        });

        configured.Should().BeTrue();
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MvcOptions>>()
            .Value.MaxModelBindingCollectionSize.Should().Be(42);
        RoutesOf(host).Should().Contain("api/spark-controllers-probe/ping");
    }
}

/// <summary>Exists only to be discovered. Named distinctly so no other host in this assembly picks it
/// up by accident.</summary>
[ApiController]
[Route("api/spark-controllers-probe")]
public sealed class SparkControllersProbeController : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok("pong");
}
