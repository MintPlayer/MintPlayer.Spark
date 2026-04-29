using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Pins the AddSparkReplication DI-shape contract (services + recipient + named HttpClients
/// registered) and the BuildDeploymentMessages projection (per-source-module envelope shape
/// the message bus broadcasts on startup). The latter is the foreach loop UseSparkReplication
/// runs in a Task.Run after registration; pulling the projection out keeps it testable
/// without spinning up a host.
/// </summary>
public class SparkReplicationExtensionsTests
{
    // --- AddSparkReplication: DI-shape -----------------------------------

    [Fact]
    public void AddSparkReplication_registers_message_bus_recipient_for_EtlScriptDeployment()
    {
        var services = new ServiceCollection();

        services.AddSparkReplication(o => o.ModuleName = "TestModule");

        services.Should().ContainSingle(d => d.ServiceType == typeof(IRecipient<EtlScriptDeploymentMessage>))
            .Which.Should().BeEquivalentTo(new
            {
                ImplementationType = typeof(EtlScriptDeploymentRecipient),
                Lifetime = ServiceLifetime.Scoped,
            });
    }

    [Fact]
    public void AddSparkReplication_registers_ModuleRegistrationService_as_singleton()
    {
        var services = new ServiceCollection();

        services.AddSparkReplication(o => o.ModuleName = "TestModule");

        services.Should().Contain(d =>
            d.ServiceType == typeof(ModuleRegistrationService) &&
            d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddSparkReplication_registers_EtlScriptCollector_and_EtlTaskManager_as_singletons()
    {
        var services = new ServiceCollection();

        services.AddSparkReplication(o => o.ModuleName = "TestModule");

        services.Should().Contain(d =>
            d.ServiceType == typeof(EtlScriptCollector) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d =>
            d.ServiceType == typeof(EtlTaskManager) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddSparkReplication_registers_ISyncActionInterceptor_scoped()
    {
        var services = new ServiceCollection();

        services.AddSparkReplication(o => o.ModuleName = "TestModule");

        services.Should().ContainSingle(d => d.ServiceType.Name == "ISyncActionInterceptor")
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddSparkReplication_invokes_configure_callback_with_options_when_resolved()
    {
        var services = new ServiceCollection();

        services.AddSparkReplication(options =>
        {
            options.ModuleName = "Captured";
            options.ModuleUrl = "http://captured.test";
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SparkReplicationOptions>>().Value;

        resolved.ModuleName.Should().Be("Captured");
        resolved.ModuleUrl.Should().Be("http://captured.test");
    }

    // --- BuildDeploymentMessages: projection contract --------------------

    [Fact]
    public void BuildDeploymentMessages_returns_one_envelope_per_source_module()
    {
        var appStore = Substitute.For<IDocumentStore>();
        appStore.Database.Returns("hr-db");
        appStore.Urls.Returns(["http://hr.test:8080"]);

        var scriptsByModule = new Dictionary<string, List<EtlScriptItem>>
        {
            ["Fleet"] = [new EtlScriptItem { SourceCollection = "Cars", Script = "// ..." }],
            ["Inventory"] = [new EtlScriptItem { SourceCollection = "Parts", Script = "// ..." }],
        };
        var options = new SparkReplicationOptions { ModuleName = "HR", ModuleUrl = "http://hr.test:8080" };

        var messages = SparkReplicationExtensions.BuildDeploymentMessages(scriptsByModule, options, appStore).ToList();

        messages.Should().HaveCount(2);
        messages.Select(m => m.SourceModuleName).Should().BeEquivalentTo(["Fleet", "Inventory"]);
    }

    [Fact]
    public void BuildDeploymentMessages_propagates_target_database_url_and_requesting_module()
    {
        var appStore = Substitute.For<IDocumentStore>();
        appStore.Database.Returns("hr-db");
        appStore.Urls.Returns(["http://hr.test:8080", "http://hr.test:8081"]);

        var scriptsByModule = new Dictionary<string, List<EtlScriptItem>>
        {
            ["Fleet"] = [new EtlScriptItem { SourceCollection = "Cars", Script = "// fleet.cars" }],
        };
        var options = new SparkReplicationOptions { ModuleName = "HR", ModuleUrl = "http://hr.test:8080" };

        var msg = SparkReplicationExtensions.BuildDeploymentMessages(scriptsByModule, options, appStore).Single();

        msg.SourceModuleName.Should().Be("Fleet");
        msg.Request.RequestingModule.Should().Be("HR");
        msg.Request.TargetDatabase.Should().Be("hr-db");
        msg.Request.TargetUrls.Should().BeEquivalentTo(new[] { "http://hr.test:8080", "http://hr.test:8081" });
        msg.Request.Scripts.Should().ContainSingle()
            .Which.SourceCollection.Should().Be("Cars");
    }

    [Fact]
    public void BuildDeploymentMessages_returns_empty_when_no_scripts_collected()
    {
        var appStore = Substitute.For<IDocumentStore>();
        var scriptsByModule = new Dictionary<string, List<EtlScriptItem>>();
        var options = new SparkReplicationOptions { ModuleName = "HR", ModuleUrl = "http://hr.test:8080" };

        SparkReplicationExtensions.BuildDeploymentMessages(scriptsByModule, options, appStore)
            .Should().BeEmpty();
    }

    // --- MapSparkReplication: trivial wrapper ----------------------------

    [Fact]
    public void MapSparkReplication_returns_same_endpoints_instance_for_chaining()
    {
        using var endpoints = NewMinimalRouteBuilder();

        var returned = endpoints.MapSparkReplication();

        returned.Should().BeSameAs(endpoints);
    }

    private static MinimalEndpointRouteBuilder NewMinimalRouteBuilder() => new();

    /// <summary>
    /// Minimal <see cref="IEndpointRouteBuilder"/> backing for the source-generated
    /// <c>MapSparkReplicationEndpoints</c> call — substitutes throw on property reads.
    /// </summary>
    private sealed class MinimalEndpointRouteBuilder : IEndpointRouteBuilder, IDisposable
    {
        private readonly ServiceProvider _sp;
        private readonly List<Microsoft.AspNetCore.Routing.EndpointDataSource> _ds = [];
        public MinimalEndpointRouteBuilder()
        {
            var s = new ServiceCollection();
            s.AddRouting();
            _sp = s.BuildServiceProvider();
        }
        public IServiceProvider ServiceProvider => _sp;
        public ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> DataSources => _ds;
        public Microsoft.AspNetCore.Builder.IApplicationBuilder CreateApplicationBuilder()
            => new Microsoft.AspNetCore.Builder.ApplicationBuilder(_sp);
        public void Dispose() => _sp.Dispose();
    }

    [Fact]
    public void BuildDeploymentMessages_does_not_carry_a_SourceModuleUrl_field()
    {
        // Pin the contract change from issue #148: the message must NOT carry the source URL.
        // The recipient resolves it from SparkModules per-delivery so retries pick up
        // freshly-registered modules.
        var properties = typeof(EtlScriptDeploymentMessage)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        properties.Should().NotContain("SourceModuleUrl");
        properties.Should().Contain("SourceModuleName");
        properties.Should().Contain("Request");
    }
}
