using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Pins the <see cref="SparkExtensions.UseSpark(IApplicationBuilder, Action{UseSparkOptions})"/>
/// overload — it composes <c>UseSpark()</c> with a per-app options callback. Model
/// synchronization used to be the only thing that rode on this seam; it has since moved to the
/// builder phase, so the overload now exists purely as a per-app middleware extension point.
/// Kept under test because <c>UseSparkOptions.App</c> not being set would silently break any
/// future option that depends on it.
/// </summary>
public class UseSparkOptionsTests : SparkTestDriver
{
    [Fact]
    public async Task UseSpark_with_options_invokes_callback_and_sets_options_App_property()
    {
        UseSparkOptions? captured = null;

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSpark(spark => spark.UseContext<TestSparkContext>());
                    // Override the AddSpark-registered IDocumentStore (it would otherwise try
                    // to connect to localhost:8080) with our embedded test store.
                    var existing = services.Single(d => d.ServiceType == typeof(IDocumentStore));
                    services.Remove(existing);
                    services.AddSingleton(Store);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseSpark(opts => captured = opts);
                    app.UseEndpoints(endpoints => endpoints.MapSpark());
                }))
            .StartAsync();

        captured.Should().NotBeNull();
        // The internal App pointer is what UseSparkOptions.SynchronizeModelsIfRequested chains
        // through, so it must reference the same IApplicationBuilder we used.
        var appProp = typeof(UseSparkOptions).GetProperty("App",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        appProp.GetValue(captured).Should().NotBeNull();
    }
}
