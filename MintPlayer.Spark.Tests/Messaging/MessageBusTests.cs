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
        => new MessageBus(Store, Options.Create(options ?? new SparkMessagingOptions()));

    [Fact]
    public async Task BroadcastAsync_persists_a_SparkMessage_with_inferred_queue_name_and_payload()
    {
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderPlaced("orders/1", 99.95m));
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var messages = await session.Query<SparkMessage>().ToListAsync();
        messages.Should().ContainSingle();
        var message = messages[0];
        message.QueueName.Should().Be(typeof(OrderPlaced).FullName);
        message.MessageType.Should().Be(typeof(OrderPlaced).AssemblyQualifiedName);
        message.Status.Should().Be(EMessageStatus.Pending);
        message.PayloadJson.Should().Contain("orders/1").And.Contain("99.95");
        message.NextAttemptAtUtc.Should().BeNull();
        message.MaxAttempts.Should().Be(5);
        message.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task BroadcastAsync_with_MessageQueue_attribute_uses_the_attribute_queue_name()
    {
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderShipped("orders/1"));
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.QueueName.Should().Be("custom-orders-queue");
    }

    [Fact]
    public async Task BroadcastAsync_with_explicit_queue_name_overrides_both_attribute_and_type_name()
    {
        var bus = NewBus();

        await bus.BroadcastAsync(new OrderShipped("orders/1"), queueName: "priority-queue");
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.QueueName.Should().Be("priority-queue");
    }

    [Fact]
    public async Task DelayBroadcastAsync_sets_NextAttemptAtUtc_to_roughly_now_plus_delay()
    {
        var bus = NewBus();

        await bus.DelayBroadcastAsync(new OrderPlaced("orders/1", 10m), TimeSpan.FromMinutes(5));
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.NextAttemptAtUtc.Should().NotBeNull();
        message.NextAttemptAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task MaxAttempts_from_options_is_written_onto_the_stored_message()
    {
        var bus = NewBus(new SparkMessagingOptions { MaxAttempts = 12 });

        await bus.BroadcastAsync(new OrderPlaced("orders/1", 10m));
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.MaxAttempts.Should().Be(12);
    }
}
