using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

public class MessageBusTests : SparkTestDriver
{
    private record OrderPlaced(string OrderId, decimal Amount);

    [MessageQueue("custom-orders-queue")]
    private record OrderShipped(string OrderId);

    private IMessageBus NewBus(SparkMessagingOptions? options = null)
    {
        // No lanes declared: the bus still needs a registry, because it asks for the partition key of
        // every message it stores. An undeclared lane is unordered, so the answer is null.
        var resolved = Options.Create(options ?? new SparkMessagingOptions());
        var services = new ServiceCollection();
        services.AddSingleton(resolved);

        return new MessageBus(
            Store,
            resolved,
            TimeProvider.System,
            new MessageSequence(TimeProvider.System),
            new LaneRegistry(services.BuildServiceProvider(), resolved));
    }

    [Fact]
    public async Task BroadcastAsync_persists_a_SparkMessage_with_inferred_queue_name_and_payload()
    {
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderPlaced("orders/1", 99.95m));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var messages = await session.Query<SparkMessage>().ToListAsync();
        messages.Should().ContainSingle();
        var message = messages[0];
        message.QueueName.Should().Be(typeof(OrderPlaced).FullName);
        message.MessageType.Should().Be(typeof(OrderPlaced).AssemblyQualifiedName);
        message.Status.Should().Be(EMessageStatus.Pending);
        message.PayloadJson.Should().Contain("orders/1").And.Contain("99.95");
        message.NextAttemptAtUtc.Should().NotHaveValue();
        message.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task BroadcastAsync_with_MessageQueue_attribute_uses_the_attribute_queue_name()
    {
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderShipped("orders/1"));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.QueueName.Should().Be("custom-orders-queue");
    }

    [Fact]
    public async Task BroadcastAsync_with_explicit_queue_name_overrides_both_attribute_and_type_name()
    {
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderShipped("orders/1"), queueName: "priority-queue");
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.QueueName.Should().Be("priority-queue");
    }

    [Fact]
    public async Task DelayBroadcastAsync_sets_VisibleAtUtc_and_leaves_the_retry_field_alone()
    {
        // A delay and a retry backoff are different things and now live in different fields. A
        // backing-off head must block its partition, or a newer message overtakes it; a delayed
        // message must not, because a delay is a scheduling instruction rather than a dependency —
        // blocking on one would freeze its whole partition for the length of the delay.
        var bus = NewBus();

        await bus.DelayBroadcastAsync(new OrderPlaced("orders/1", 10m), TimeSpan.FromMinutes(5));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.VisibleAtUtc.Should().HaveValue();
        message.VisibleAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
        message.NextAttemptAtUtc.Should().NotHaveValue(
            "a delayed message has not been attempted, so it is not on a retry rung");
    }

    [Fact]
    public async Task DelayBroadcastAsync_stores_the_message_unwoken()
    {
        // Issue #233: a delayed message must not match the subscription query until
        // MessageRetrySweeper wakes it up after the delay elapses.
        var bus = NewBus();

        await bus.DelayBroadcastAsync(new OrderPlaced("orders/1", 10m), TimeSpan.FromMinutes(5));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.Status.Should().Be(EMessageStatus.Pending);
    }

    [Fact]
    public async Task Broadcast_order_is_recorded_as_a_strictly_increasing_sequence()
    {
        // Retry policy is deliberately NOT snapshotted onto the message any more: it resolves at
        // scheduling time so that a configuration change — the flat-5s test override in particular —
        // reaches messages that are already in flight. What the producer does stamp is the ordering
        // key, because that must never be recomputed.
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderPlaced("orders/1", 10m));
        await bus.BroadcastAsync(new OrderPlaced("orders/2", 20m));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var messages = await session.Query<SparkMessage>().OrderBy(m => m.Sequence).ToListAsync();

        messages.Should().HaveCount(2);
        messages[0].PayloadJson.Should().Contain("orders/1");
        messages[1].Sequence.Should().BeGreaterThan(messages[0].Sequence);
    }
}
