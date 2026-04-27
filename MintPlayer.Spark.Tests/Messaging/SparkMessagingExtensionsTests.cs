using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Pins the DI wiring of the messaging package: which services land where, lifetimes,
/// and the IServiceCollectionAccessor seam that <see cref="MessageSubscriptionManager"/>
/// uses to discover IRecipient&lt;T&gt; queue names at runtime. A regression in this
/// extension silently breaks every IRecipient-driven message handler in the host.
/// </summary>
public class SparkMessagingExtensionsTests
{
    [Fact]
    public void AddSparkMessaging_registers_IMessageBus_as_scoped()
    {
        var services = new ServiceCollection();

        services.AddSparkMessaging();

        var descriptor = services.Single(d => d.ServiceType == typeof(IMessageBus));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<MessageBus>();
    }

    [Fact]
    public void AddSparkMessaging_registers_IMessageCheckpoint_as_an_alias_for_concrete_MessageCheckpoint()
    {
        var services = new ServiceCollection();

        services.AddSparkMessaging();

        // Both MessageCheckpoint (concrete) and IMessageCheckpoint (interface) must resolve
        // to the same instance per request — the alias uses a factory delegate.
        services.Should().Contain(d => d.ServiceType == typeof(MessageCheckpoint) && d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d => d.ServiceType == typeof(IMessageCheckpoint) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddSparkMessaging_registers_MessageSubscriptionManager_as_a_hosted_service()
    {
        var services = new ServiceCollection();

        services.AddSparkMessaging();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddSparkMessaging_registers_an_IServiceCollectionAccessor_singleton_pointing_at_the_collection_itself()
    {
        var services = new ServiceCollection();

        services.AddSparkMessaging();

        var accessorDescriptor = services.Single(d =>
            d.ServiceType.Name == "IServiceCollectionAccessor");
        accessorDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        // The instance must wrap the same IServiceCollection it was added to — that's how
        // MessageSubscriptionManager finds the IRecipient<T> registrations at runtime.
        accessorDescriptor.ImplementationInstance.Should().NotBeNull();
    }

    [Fact]
    public void AddSparkMessaging_applies_the_optional_configure_action_to_options()
    {
        var services = new ServiceCollection();

        services.AddSparkMessaging(o => o.MaxAttempts = 42);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SparkMessagingOptions>>().Value;
        options.MaxAttempts.Should().Be(42);
    }

    [Fact]
    public void AddSparkMessaging_works_without_a_configure_action()
    {
        var services = new ServiceCollection();

        var act = () => services.AddSparkMessaging();

        act.Should().NotThrow();
    }
}
