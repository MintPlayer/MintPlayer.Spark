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
/// (covered by <see cref="SparkMessagingExtensionsTests"/>), and that it deploys no index at startup: since #536 D40
/// the feeder and the sweeper read with plain queries, which every store answers.
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
    public void AddMessaging_queues_no_startup_middleware()
    {
        var builder = NewBuilder();

        builder.AddMessaging();

        // The index-creation middleware is gone (#536 D40). An action queued here would read the app's services and
        // throw on the null below.
        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(_ => null!);
        var act = () => builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);
        act.Should().NotThrow();
    }
}
