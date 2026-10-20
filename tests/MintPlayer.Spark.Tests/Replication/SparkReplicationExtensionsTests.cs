using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Pins the shape <see cref="SparkReplicationExtensions.AddSparkReplication"/> registers (warehouse §4.2, WM1): the
/// recipients of the one <c>spark-replication</c> queue, the catalog as the framework's replica-type guard, and —
/// the point of the redesign — no hosted service of its own (the SyncAction subscription worker and its sweeper are
/// gone; replication rides messaging's subscription).
/// </summary>
public class SparkReplicationExtensionsTests
{
    private static ServiceCollection Registered()
    {
        var services = new ServiceCollection();
        services.AddSparkReplication(o => o.ModuleName = "TestModule");
        return services;
    }

    [Fact]
    public void Every_replication_message_has_a_scoped_recipient()
    {
        var services = Registered();

        foreach (var (message, recipient) in new[]
        {
            (typeof(ReplicaRegistrationMessage), typeof(ReplicaRegistrationRecipient)),
            (typeof(ReplicaDeployMessage), typeof(ReplicaDeployRecipient)),
            (typeof(ReplicaPushMessage), typeof(ReplicaPushRecipient)),
            (typeof(ReplicaReconcileMessage), typeof(ReplicaReconcileRecipient)),
            (typeof(SyncActionDispatchMessage), typeof(SyncActionDispatchRecipient)),
            (typeof(ReplicaChangedMessage), typeof(ReplicaChangedRecipient)),
        })
        {
            services.Should().ContainSingle(d => d.ServiceType == typeof(IRecipient<>).MakeGenericType(message))
                .Which.Should().BeEquivalentTo(new { ImplementationType = recipient, Lifetime = ServiceLifetime.Scoped });
        }
    }

    /// <summary>
    /// Red on e0c542f9: <c>SyncActionSubscriptionWorker</c> and <c>SyncActionRetrySweeper</c> were hosted services, the
    /// worker a second RavenDB subscription (2 of Community's 3 with messaging). Replication now registers none.
    /// </summary>
    [Fact]
    public void Replication_registers_no_hosted_service()
    {
        Registered().Should().NotContain(d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void All_replication_messages_share_the_one_queue()
    {
        foreach (var message in new[] { typeof(ReplicaRegistrationMessage), typeof(ReplicaDeployMessage), typeof(ReplicaPushMessage),
                     typeof(ReplicaReconcileMessage), typeof(SyncActionDispatchMessage), typeof(ReplicaChangedMessage) })
        {
            message.GetCustomAttributes(typeof(MessageQueueAttribute), false).Cast<MessageQueueAttribute>()
                .Should().ContainSingle().Which.QueueName.Should().Be(ReplicationQueue.Name);
        }
    }

    [Fact]
    public void The_catalog_is_the_frameworks_replica_type_guard()
    {
        using var provider = Registered().BuildServiceProvider();

        provider.GetRequiredService<ISparkReplicaTypes>().Should().BeSameAs(provider.GetRequiredService<ReplicaCatalog>());
    }

    [Fact]
    public void AddSparkReplication_registers_ModuleRegistrationService_and_the_ETL_services_as_singletons()
    {
        var services = Registered();

        services.Should().Contain(d => d.ServiceType == typeof(ModuleRegistrationService) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d => d.ServiceType == typeof(IEtlTaskManager) && d.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(d => d.ServiceType == typeof(IReplicationTransportResolver) && d.Lifetime == ServiceLifetime.Singleton);
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

    /// <summary>
    /// F19: the consumer never tells the owner where to send the data, and never sends anything but the generated
    /// constants: the owner takes the destination from the registry and re-renders the script.
    /// </summary>
    [Fact]
    public void The_registration_names_no_destination()
    {
        typeof(ReplicaRegistrationRequest).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["RequestingModule", "Items"]);
        typeof(ReplicaRegistrationItem).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["TargetCollection", "SourceCollection", "Manifest", "Script", "Hash"]);
    }

    /// <summary>#148: the registration message carries no URL; the recipient resolves it from SparkModules per delivery.</summary>
    [Fact]
    public void The_registration_message_carries_no_url()
        => typeof(ReplicaRegistrationMessage).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Url"));

    [Fact]
    public void MapSparkReplication_returns_same_endpoints_instance_for_chaining()
    {
        using var endpoints = new MinimalEndpointRouteBuilder();

        var returned = endpoints.MapSparkReplication();

        returned.Should().BeSameAs(endpoints);
    }

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
}
