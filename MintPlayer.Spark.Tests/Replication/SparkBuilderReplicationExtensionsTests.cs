using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Public-surface entry point for the replication package. Pins that AddReplication
/// delegates to <see cref="SparkReplicationExtensions.AddSparkReplication"/> (covered
/// elsewhere) and that the deferred middleware/endpoint registrations both land on the
/// shared <see cref="SparkModuleRegistry"/> so <c>UseSpark()</c>/<c>MapSpark()</c> activate
/// the startup work and the ETL endpoints.
/// </summary>
public class SparkBuilderReplicationExtensionsTests
{
    private static SparkBuilder NewBuilder() => new(new ServiceCollection());

    [Fact]
    public void AddReplication_returns_same_builder_for_chaining()
    {
        var builder = NewBuilder();

        var returned = builder.AddReplication(o => o.ModuleName = "Mod");

        returned.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddReplication_registers_core_replication_services()
    {
        var builder = NewBuilder();

        builder.AddReplication(o => o.ModuleName = "Mod");

        // Spot-check the registrations AddSparkReplication contributes; the full DI shape
        // is pinned in SparkReplicationExtensionsTests.
        builder.Services.Should().Contain(d => d.ServiceType == typeof(ModuleRegistrationService));
        builder.Services.Should().Contain(d => d.ServiceType == typeof(EtlScriptCollector));
        builder.Services.Should().Contain(d => d.ServiceType == typeof(EtlTaskManager));
    }

    [Fact]
    public void AddReplication_propagates_configure_callback_to_options()
    {
        var builder = NewBuilder();

        builder.AddReplication(o =>
        {
            o.ModuleName = "Captured";
            o.ModuleUrl = "http://captured.test";
        });

        var resolved = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<SparkReplicationOptions>>().Value;
        resolved.ModuleName.Should().Be("Captured");
        resolved.ModuleUrl.Should().Be("http://captured.test");
    }

    [Fact]
    public void AddReplication_queues_a_middleware_action_that_only_fires_on_WebApplication()
    {
        var builder = NewBuilder();

        builder.AddReplication(o => o.ModuleName = "Mod");

        // Non-WebApplication path takes the early-return branch; the action must run safely.
        var app = Substitute.For<IApplicationBuilder>();
        var act = () => builder.Registry.ApplyMiddleware(app);
        act.Should().NotThrow();
    }

    [Fact]
    public void AddReplication_queues_an_endpoint_action_that_invokes_MapSparkReplication()
    {
        var builder = NewBuilder();

        builder.AddReplication(o => o.ModuleName = "Mod");

        // The action calls MapSparkReplication → endpoints.MapSparkReplicationEndpoints(),
        // which probes IEndpointRouteBuilder for ServiceProvider/DataSources. Stand up
        // a minimal real route builder so the call chain doesn't NRE on a substitute.
        using var endpoints = new MinimalEndpointRouteBuilder();
        var act = () => builder.Registry.MapEndpoints(endpoints);
        act.Should().NotThrow();
        endpoints.Touched.Should().BeTrue();
    }

    /// <summary>
    /// Minimal real <see cref="IEndpointRouteBuilder"/>. Substitutes throw on the property
    /// reads MapSparkReplicationEndpoints performs (ServiceProvider for resolving routing,
    /// DataSources for adding the generated endpoints).
    /// </summary>
    private sealed class MinimalEndpointRouteBuilder : IEndpointRouteBuilder, IDisposable
    {
        private readonly ServiceProvider _sp;
        private readonly List<EndpointDataSource> _dataSources = [];
        public bool Touched { get; private set; }

        public MinimalEndpointRouteBuilder()
        {
            var services = new ServiceCollection();
            services.AddRouting();
            _sp = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get { Touched = true; return _sp; } }
        public ICollection<EndpointDataSource> DataSources { get { Touched = true; return _dataSources; } }
        public IApplicationBuilder CreateApplicationBuilder() { Touched = true; return new ApplicationBuilder(_sp); }

        public void Dispose() => _sp.Dispose();
    }
}
