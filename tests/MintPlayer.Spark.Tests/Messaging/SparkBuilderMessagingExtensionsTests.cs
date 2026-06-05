using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Public-surface entry point for the messaging package — the only thing host apps call.
/// Pins that AddMessaging delegates to <see cref="SparkMessagingExtensions.AddSparkMessaging"/>
/// (covered by <see cref="SparkMessagingExtensionsTests"/>) and queues the index-creation
/// middleware so <c>UseSpark()</c> wires the SparkMessages_ByQueue index at startup.
/// </summary>
public class SparkBuilderMessagingExtensionsTests
{
    private static SparkBuilder NewBuilder() => new(new ServiceCollection());

    [Fact]
    public void AddMessaging_returns_same_builder_for_chaining()
    {
        var builder = NewBuilder();

        var returned = builder.AddMessaging();

        returned.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddMessaging_registers_messaging_services_via_AddSparkMessaging()
    {
        var builder = NewBuilder();

        builder.AddMessaging();

        // Spot-check: AddSparkMessaging registers IMessageBus + the subscription manager hosted service.
        builder.Services.Should().Contain(d => d.ServiceType == typeof(IMessageBus));
    }

    [Fact]
    public void AddMessaging_propagates_optional_configure_callback_to_options()
    {
        var builder = NewBuilder();

        builder.AddMessaging(o => o.MaxAttempts = 7);

        var resolved = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<SparkMessagingOptions>>().Value;
        resolved.MaxAttempts.Should().Be(7);
    }

    [Fact]
    public void AddMessaging_queues_one_middleware_action_for_index_creation()
    {
        var builder = NewBuilder();

        builder.AddMessaging();

        // We can't trigger the actual index deploy here (it needs a real IDocumentStore on
        // the app's services), but we can verify the registry has *something* queued and
        // that ApplyMiddleware reaches it.
        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(_ => null!); // force a NullReferenceException inside the action so it bubbles back
        var act = () => builder.Registry.ApplyMiddleware(app);
        act.Should().Throw<Exception>(); // any throw confirms the action ran
    }
}
