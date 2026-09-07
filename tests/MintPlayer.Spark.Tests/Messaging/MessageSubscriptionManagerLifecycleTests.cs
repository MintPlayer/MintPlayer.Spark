using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Pins <see cref="MessageSubscriptionManager"/>'s start-up lifecycle in both subscription modes.
/// <para>
/// This class previously asserted that the manager "starts a worker per discovered queue" and that
/// a subscription named after the queue appears on the server. That premise is exactly what the
/// single-subscription rework deletes, so the fact was rewritten rather than adapted: in the
/// default mode the assertion is now the opposite one — <b>one</b> subscription exists, it is named
/// <c>SparkMessaging</c>, and no per-queue definition is created however many queues are
/// discovered.
/// </para>
/// </summary>
public class MessageSubscriptionManagerLifecycleTests : SparkTestDriver
{
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    [Fact]
    public async Task Logs_a_warning_and_returns_when_no_IRecipient_is_registered()
    {
        // No IRecipient<T> registered → DiscoverQueueNames yields an empty set →
        // ExecuteAsync hits the early-return branch.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);

        await cts.CancelAsync();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SingleSubscription_mode_creates_exactly_one_subscription_named_SparkMessaging()
    {
        var provider = BuildProvider(ESubscriptionMode.SingleSubscription,
            registerSecondQueue: true);
        await using var _ = provider;

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            // Wait on the observable signal — the subscription existing on the server — rather
            // than sleeping and hoping. A fixed delay here used to make this pass whether or not
            // anything was ever created.
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128)
                    .Any(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant),
                $"the manager to create the shared '{MessageFeeder.SubscriptionNameConstant}' subscription",
                TimeSpan.FromSeconds(10));

            var all = Store.Subscriptions.GetSubscriptions(0, 128);

            // The point of the rework: two queues, still one subscription.
            all.Should().ContainSingle(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant);
            all.Where(s => s.SubscriptionName?.StartsWith("SparkMessaging-", StringComparison.Ordinal) == true)
                .Should().BeEmpty("no per-queue definition may be created in SingleSubscription mode");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SubscriptionPerQueue_mode_still_creates_one_subscription_per_queue()
    {
        var provider = BuildProvider(ESubscriptionMode.SubscriptionPerQueue,
            registerSecondQueue: true);
        await using var _ = provider;

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128)
                    .Count(s => s.SubscriptionName?.StartsWith("SparkMessaging-", StringComparison.Ordinal) == true) >= 2,
                "the manager to create a per-queue subscription for both queues",
                TimeSpan.FromSeconds(20));

            var perQueue = Store.Subscriptions.GetSubscriptions(0, 128)
                .Where(s => s.SubscriptionName?.StartsWith("SparkMessaging-", StringComparison.Ordinal) == true)
                .Select(s => s.SubscriptionName!)
                .ToList();

            perQueue.Should().Contain($"SparkMessaging-{FirstQueue}");
            perQueue.Should().Contain($"SparkMessaging-{SecondQueue}");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Legacy_per_queue_definitions_are_pruned_on_startup_and_the_shared_one_survives()
    {
        // Two stale definitions of the shape the old design created, plus a name that must NOT be
        // touched. The prefix guard is one character wide — "SparkMessaging" versus
        // "SparkMessaging-" — so this asserts the boundary directly.
        await Store.Subscriptions.CreateAsync(new Raven.Client.Documents.Subscriptions.SubscriptionCreationOptions
        {
            Name = "SparkMessaging-StaleQueueOne",
            Query = "from SparkMessages"
        });
        await Store.Subscriptions.CreateAsync(new Raven.Client.Documents.Subscriptions.SubscriptionCreationOptions
        {
            Name = "SparkMessaging-StaleQueueTwo",
            Query = "from SparkMessages"
        });

        var provider = BuildProvider(ESubscriptionMode.SingleSubscription, registerSecondQueue: false);
        await using var _ = provider;

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128)
                    .Any(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant),
                "the shared subscription to be created after the prune",
                TimeSpan.FromSeconds(10));

            var names = Store.Subscriptions.GetSubscriptions(0, 128)
                .Select(s => s.SubscriptionName!)
                .ToList();

            names.Should().NotContain("SparkMessaging-StaleQueueOne");
            names.Should().NotContain("SparkMessaging-StaleQueueTwo");
            names.Should().Contain(MessageFeeder.SubscriptionNameConstant,
                "the shared subscription has no trailing hyphen, so the legacy prefix must not match it");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private ServiceProvider BuildProvider(ESubscriptionMode mode, bool registerSecondQueue)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging(o => o.SubscriptionMode = mode);
        services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>, TestPingRecipient>();
        if (registerSecondQueue)
            services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPong>, TestPongRecipient>();
        return services.BuildServiceProvider();
    }

    private const string FirstQueue = "MessageSubscriptionManagerLifecycleTests-Ping";
    private const string SecondQueue = "MessageSubscriptionManagerLifecycleTests-Pong";

    [MintPlayer.Spark.Messaging.Abstractions.MessageQueueAttribute(FirstQueue)]
    public sealed class TestPing
    {
        public string? Hello { get; set; }
    }

    [MintPlayer.Spark.Messaging.Abstractions.MessageQueueAttribute(SecondQueue)]
    public sealed class TestPong
    {
        public string? Hello { get; set; }
    }

    private sealed class TestPingRecipient : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>
    {
        public Task HandleAsync(TestPing message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestPongRecipient : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPong>
    {
        public Task HandleAsync(TestPong message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
