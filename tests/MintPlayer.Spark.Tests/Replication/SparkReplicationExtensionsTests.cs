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
    public void AddSparkReplication_registers_the_contract_and_ETL_services_as_singletons()
    {
        var services = new ServiceCollection();

        services.AddSparkReplication(o => o.ModuleName = "TestModule");

        services.Should().Contain(d =>
            d.ServiceType == typeof(ReplicaContractCollector) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d =>
            d.ServiceType == typeof(ReplicationContractCatalog) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d =>
            d.ServiceType == typeof(IRavenEtlLicence) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d =>
            d.ServiceType == typeof(EtlTaskManager) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d =>
            d.ServiceType == typeof(IEtlTaskManager) && d.Lifetime == ServiceLifetime.Singleton);
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
        var contractsByModule = new Dictionary<string, List<EtlContractRequest>>
        {
            ["Fleet"] = [new EtlContractRequest { Contract = "cars", TargetCollection = "Cars" }],
            ["Inventory"] = [new EtlContractRequest { Contract = "parts", TargetCollection = "Parts" }],
        };
        var options = new SparkReplicationOptions { ModuleName = "HR", ModuleUrl = "http://hr.test:8080" };

        var messages = SparkReplicationExtensions.BuildDeploymentMessages(contractsByModule, options).ToList();

        messages.Should().HaveCount(2);
        messages.Select(m => m.SourceModuleName).Should().BeEquivalentTo(["Fleet", "Inventory"]);
    }

    [Fact]
    public void BuildDeploymentMessages_carries_the_requesting_module_and_its_contracts()
    {
        var contractsByModule = new Dictionary<string, List<EtlContractRequest>>
        {
            ["Fleet"] = [new EtlContractRequest { Contract = "cars", Version = 2, TargetCollection = "Cars" }],
        };
        var options = new SparkReplicationOptions { ModuleName = "HR", ModuleUrl = "http://hr.test:8080" };

        var msg = SparkReplicationExtensions.BuildDeploymentMessages(contractsByModule, options).Single();

        msg.SourceModuleName.Should().Be("Fleet");
        msg.Request.RequestingModule.Should().Be("HR");
        msg.Request.Contracts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Contract = "cars", Version = 2, TargetCollection = "Cars" });
    }

    /// <summary>
    /// F19: the consumer no longer tells the owner where to send the data. The owner takes the
    /// destination from the consumer's registry entry, so the request has nowhere to put one.
    /// </summary>
    [Fact]
    public void The_deployment_request_names_no_destination_and_no_script()
    {
        typeof(EtlDeploymentRequest).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["RequestingModule", "Contracts"]);
    }

    [Fact]
    public void BuildDeploymentMessages_returns_empty_when_no_contracts_collected()
    {
        var contractsByModule = new Dictionary<string, List<EtlContractRequest>>();
        var options = new SparkReplicationOptions { ModuleName = "HR", ModuleUrl = "http://hr.test:8080" };

        SparkReplicationExtensions.BuildDeploymentMessages(contractsByModule, options)
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
