using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// <see cref="IMessageRecipientRegistry"/> answers "does anything in this app consume messages of
/// this type", so a publisher that would otherwise broadcast speculatively can ask first.
/// <para>
/// The cost of getting this wrong is silent and cumulative: queue workers are started per registered
/// recipient, so a message broadcast to a type nobody consumes stores a document that nothing ever
/// drains — one per publish, forever. The GitHub webhooks package offers both a typed and an untyped
/// envelope for every delivery, and every app subscribing to only one of them was paying for both.
/// </para>
/// </summary>
public class MessageRecipientRegistryTests : SparkTestDriver
{
    public sealed class ConsumedMessage { }
    public sealed class UnconsumedMessage { }
    public sealed class Envelope<T> { }

    public sealed class ConsumedRecipient : IRecipient<ConsumedMessage>
    {
        public Task HandleAsync(ConsumedMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class ClosedGenericRecipient : IRecipient<Envelope<ConsumedMessage>>
    {
        public Task HandleAsync(Envelope<ConsumedMessage> message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private ServiceProvider BuildProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_type_with_a_registered_recipient_is_consumed()
    {
        using var provider = BuildProvider(s => s.AddScoped<IRecipient<ConsumedMessage>, ConsumedRecipient>());
        var registry = provider.GetRequiredService<IMessageRecipientRegistry>();

        registry.HasRecipient<ConsumedMessage>().Should().BeTrue();
        registry.HasRecipient(typeof(ConsumedMessage)).Should().BeTrue();
    }

    [Fact]
    public void A_type_with_no_recipient_is_not_consumed()
    {
        using var provider = BuildProvider(s => s.AddScoped<IRecipient<ConsumedMessage>, ConsumedRecipient>());
        var registry = provider.GetRequiredService<IMessageRecipientRegistry>();

        registry.HasRecipient<UnconsumedMessage>().Should().BeFalse();
    }

    /// <summary>
    /// The distinction the webhooks package depends on: subscribing to one closed generic must not
    /// make every other closed generic of the same open type look consumed, or the suppression this
    /// registry exists for would never suppress anything.
    /// </summary>
    [Fact]
    public void Closed_generics_are_distinct_keys()
    {
        using var provider = BuildProvider(s => s.AddScoped<IRecipient<Envelope<ConsumedMessage>>, ClosedGenericRecipient>());
        var registry = provider.GetRequiredService<IMessageRecipientRegistry>();

        registry.HasRecipient<Envelope<ConsumedMessage>>().Should().BeTrue();
        registry.HasRecipient<Envelope<UnconsumedMessage>>().Should().BeFalse();
    }

    [Fact]
    public void A_null_type_is_not_consumed()
    {
        using var provider = BuildProvider(_ => { });
        provider.GetRequiredService<IMessageRecipientRegistry>().HasRecipient(null).Should().BeFalse();
    }

    /// <summary>
    /// The registry is the queue-discovery scan's owner, so an app with no recipients at all must
    /// still resolve it rather than throwing — that is the shape a fresh app has at startup.
    /// </summary>
    [Fact]
    public void An_app_with_no_recipients_resolves_an_empty_registry()
    {
        using var provider = BuildProvider(_ => { });
        var registry = provider.GetRequiredService<IMessageRecipientRegistry>();

        registry.HasRecipient<ConsumedMessage>().Should().BeFalse();
    }
}
