using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// F2. The operator guide documents <c>Spark:Replication:*</c>, including the whole
    /// <c>ClientCertificate</c> node that turns mTLS on. Nothing bound it: hosts hand-mapped four
    /// properties by name, so an operator could follow the guide exactly, restart, and get zero
    /// behaviour change and no error — with the mode silently left at its default.
    /// <para>
    /// <c>ClientCertificate.Mode</c> is the assertion that matters, because it is the key whose
    /// silent absence means "authentication is not doing what the operator configured".
    /// </para>
    /// </summary>
    [Fact]
    public void AddReplication_binds_the_documented_configuration_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:Replication:ModuleName"] = "Fleet",
            ["Spark:Replication:ModuleUrl"] = "https://fleet.internal:5101",
            ["Spark:Replication:SparkModulesDatabase"] = "SharedModules",
            ["Spark:Replication:ClientCertificate:Mode"] = "Production",
            ["Spark:Replication:ClientCertificate:Thumbprint"] = "AB12CD34",
            ["Spark:Replication:ClientCertificate:CertificateFile"] = "/secrets/Fleet.pfx",
            ["Spark:Replication:ClientCertificate:PerTargetOverrides:Audit:CertificateFile"] = "/secrets/Fleet-to-Audit.pfx",
        }).Build();

        var builder = new SparkBuilder(new ServiceCollection(), configuration);
        builder.AddReplication(_ => { });

        var resolved = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<SparkReplicationOptions>>().Value;

        resolved.ModuleName.Should().Be("Fleet");
        resolved.ModuleUrl.Should().Be("https://fleet.internal:5101");
        resolved.SparkModulesDatabase.Should().Be("SharedModules");
        resolved.ClientCertificate.Mode.Should().Be(SparkReplicationCertificateMode.Production);
        resolved.ClientCertificate.Thumbprint.Should().Be("AB12CD34");
        resolved.ClientCertificate.CertificateFile.Should().Be("/secrets/Fleet.pfx");
        resolved.ClientCertificate.PerTargetOverrides.Should().ContainKey("Audit");
        resolved.ClientCertificate.PerTargetOverrides["Audit"].CertificateFile
            .Should().Be("/secrets/Fleet-to-Audit.pfx");
    }

    /// <summary>
    /// Code wins over configuration, so a host can still override a bound value — and, more to
    /// the point, can set what JSON cannot express (the assemblies to scan) without having to
    /// restate everything else.
    /// </summary>
    [Fact]
    public void AddReplication_lets_the_configure_callback_override_bound_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:Replication:ModuleName"] = "FromConfig",
            ["Spark:Replication:ModuleUrl"] = "https://config.test",
        }).Build();

        var builder = new SparkBuilder(new ServiceCollection(), configuration);
        builder.AddReplication(o => o.ModuleName = "FromCode");

        var resolved = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<SparkReplicationOptions>>().Value;

        resolved.ModuleName.Should().Be("FromCode");
        resolved.ModuleUrl.Should().Be("https://config.test", "an override of one key must not blank the rest");
    }

    [Fact]
    public void AddReplication_works_without_any_configuration()
    {
        var builder = NewBuilder();

        var act = () => builder.AddReplication(o => o.ModuleName = "Mod");

        act.Should().NotThrow("ISparkBuilder.Configuration is optional");
    }

    [Fact]
    public void AddReplication_queues_a_middleware_action_that_only_fires_on_WebApplication()
    {
        var builder = NewBuilder();

        builder.AddReplication(o => o.ModuleName = "Mod");

        // Non-WebApplication path takes the early-return branch; the action must run safely.
        var app = Substitute.For<IApplicationBuilder>();
        var act = () => builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);
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
