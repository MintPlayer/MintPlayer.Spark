using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// However many lanes an application declares, messaging creates exactly <b>one</b> RavenDB
/// subscription.
/// </summary>
/// <remarks>
/// <para>
/// This is the invariant the whole refactor exists to hold. RavenDB permits three data subscriptions
/// per database — the same on the unlicensed and Community tiers — and exceeding that limit fails
/// <i>silently</i>: the create is refused, the worker opens against a subscription that does not
/// exist, and the queue is dead with no health signal. A production application ran that way for
/// months with three of its six queues never delivering.
/// </para>
/// <para>
/// <b>This test does not assert the cap itself, deliberately.</b> Two independent reasons make that
/// dishonest here: the local server and CI both use a <i>Developer</i> licence, which has no
/// subscription cap at all, so the assertion would pass vacuously wherever it runs; and even against
/// a capped server the limit is not enforced for roughly the first minute after startup, so it would
/// need a sleep of that length to be meaningful.
/// </para>
/// <para>
/// The cap is environmental. What the framework owes is "create one subscription", and that is
/// licence-independent, instant, and exactly what is checked below.
/// </para>
/// </remarks>
public class MessagingSubscriptionCountTests : SparkTestDriver
{
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    [MessageQueue("count-alpha")]
    public record Alpha(string Id);

    [MessageQueue("count-beta")]
    public record Beta(string Id);

    [MessageQueue("count-gamma")]
    public record Gamma(string Id);

    /// <summary>No attribute: its lane name is derived from the type, which is a fourth lane.</summary>
    public record Derived(string Id);

    private sealed class Sink<T> : IRecipient<T>
    {
        public int Calls;
        public Task HandleAsync(T message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Many_lanes_produce_exactly_one_RavenDB_subscription()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRecipient<Alpha>>(new Sink<Alpha>());
        services.AddSingleton<IRecipient<Beta>>(new Sink<Beta>());
        services.AddSingleton<IRecipient<Gamma>>(new Sink<Gamma>());
        services.AddSingleton<IRecipient<Derived>>(new Sink<Derived>());
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        await using var provider = services.BuildServiceProvider();

        var registry = new LaneRegistry();
        // A fifth lane, declared but with no recipient — the kind a framework package contributes.
        new MessagingLaneBuilder(registry).Queue("count-framework").Concurrent(1);

        var options = Options.Create(new SparkMessagingOptions());
        var discovery = new MessageLaneDiscovery(provider.GetRequiredService<IServiceCollectionAccessor>());

        discovery.DiscoverLaneNames().Should().HaveCount(4, "four message types, four lanes");

        var feeder = new MessageFeeder(
            Store,
            registry,
            new MessageProcessor(Store, provider, options, TimeProvider.System, NullLogger<MessageProcessor>.Instance),
            discovery,
            options,
            TimeProvider.System,
            NullLoggerFactory.Instance);

        await feeder.StartAsync(CancellationToken.None);
        try
        {
            // Positive signal — wait until the subscription has actually been created, rather than
            // sleeping and hoping.
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128).Count > 0,
                "the messaging subscription to be created",
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(100));

            var names = Store.Subscriptions.GetSubscriptions(0, 128).Select(s => s.SubscriptionName).ToList();

            names.Should().Equal(
                ["SparkMessaging"],
                "five lanes must cost exactly one subscription — the previous design created one per "
                + "queue, which is how six queues silently became three working ones");
        }
        finally
        {
            await feeder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Every_lane_is_served_by_that_one_subscription()
    {
        // Counting subscriptions is only half the claim: one subscription that serves three of five
        // lanes would satisfy the test above and still be the bug. So broadcast on every lane,
        // including one whose name no recipient declares, and require all of them to be handled.
        var alpha = new Sink<Alpha>();
        var beta = new Sink<Beta>();
        var derived = new Sink<Derived>();

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services =>
            {
                services.AddSingleton<IRecipient<Alpha>>(alpha);
                services.AddSingleton<IRecipient<Beta>>(beta);
                services.AddSingleton<IRecipient<Derived>>(derived);
            });

        var lanes = new[] { "count-alpha", "count-beta", typeof(Derived).FullName!, "count-adhoc" };
        foreach (var lane in lanes)
            host.StartLane(lane);

        await host.Bus.BroadcastAsync(new Alpha("a"));
        await host.Bus.BroadcastAsync(new Beta("b"));
        await host.Bus.BroadcastAsync(new Derived("d"));
        // An ad-hoc lane name no recipient declares. This overload was documented before lanes
        // shared a subscription but never worked: the name got no worker, so the message sat Pending
        // forever.
        await host.Bus.BroadcastAsync(new Alpha("adhoc"), "count-adhoc");
        host.RingAll();

        await AsyncWait.ForAsync(
            async () =>
            {
                using var session = Store.OpenAsyncSession();
                return await session.Query<SparkMessage>().ToListAsync();
            },
            messages => messages.Count == 4 && messages.All(m => m.Status == EMessageStatus.Completed),
            "every lane's message to be handled",
            last => $"[{string.Join(", ", last?.Select(m => $"{m.QueueName}={m.Status}") ?? [])}]",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(100));

        alpha.Calls.Should().Be(2, "one on its own lane and one on the ad-hoc lane");
        beta.Calls.Should().Be(1);
        derived.Calls.Should().Be(1, "a message type with no [MessageQueue] still gets a lane");
    }
}
