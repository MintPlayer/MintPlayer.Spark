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
        message.MaxAttempts.Should().Be(5);
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

    /// <summary>
    /// Replaces the deleted <c>BroadcastAsync(message, queueName)</c> fact. That overload let a
    /// caller put a message on a queue name the consumer side could not know about: the manager
    /// discovers queues by reflecting over <c>IRecipient&lt;T&gt;</c> registrations and derives the
    /// name from the type, so an override that disagreed produced documents no worker ever
    /// selected — enqueued for ever, consumed by nobody, with the app reporting itself healthy. The
    /// queue name is a property of the message type, and now only <c>[MessageQueue]</c> sets it.
    /// </summary>
    [Fact]
    public async Task BroadcastOnceAsync_enqueues_once_per_deduplication_key()
    {
        var bus = NewBus();

        await bus.BroadcastOnceAsync(new OrderShipped("orders/1"), "delivery-abc");
        await bus.BroadcastOnceAsync(new OrderShipped("orders/1"), "delivery-abc");
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var messages = await session.Query<SparkMessage>().ToListAsync();

        messages.Should().ContainSingle("the second call carries a key already enqueued");
        messages[0].Id.Should().Be(MessageBus.DeduplicationId(typeof(OrderShipped), "delivery-abc"));
        messages[0].Id.Should().StartWith("SparkMessages/delivery-abc.", "the key stays readable in the id");
    }

    /// <summary>
    /// The bug: the id was <c>SparkMessages/{sanitized key}</c>, so keys differing only in characters
    /// an id cannot hold, or only in letter case (RavenDB ids are case-insensitive), or used by two
    /// message types, all mapped to one document — and the second publish was silently dropped.
    /// </summary>
    [Theory]
    [InlineData("a:b", "a/b")]
    [InlineData("a:b", "a_b")]
    [InlineData("Delivery-1", "delivery-1")]
    public async Task BroadcastOnceAsync_keeps_keys_apart_that_used_to_sanitize_to_the_same_id(string first, string second)
    {
        var bus = NewBus();

        await bus.BroadcastOnceAsync(new OrderShipped("orders/1"), first);
        await bus.BroadcastOnceAsync(new OrderShipped("orders/2"), second);
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        (await session.Query<SparkMessage>().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task BroadcastOnceAsync_namespaces_the_key_by_message_type()
    {
        var bus = NewBus();

        await bus.BroadcastOnceAsync(new OrderShipped("orders/1"), "delivery-abc");
        await bus.BroadcastOnceAsync(new OrderPlaced("orders/1", 1m), "delivery-abc");
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        (await session.Query<SparkMessage>().CountAsync()).Should().Be(2, "two message types may use one delivery id");
    }

    [Fact]
    public void The_deduplication_id_is_bounded_whatever_the_key_length()
    {
        var id = MessageBus.DeduplicationId(typeof(OrderShipped), new string('x', 5000));

        id.Length.Should().Be("SparkMessages/".Length + MessageBus.MaxReadableKeyLength + 1 + 32);
    }

    [Fact]
    public async Task BroadcastOptions_are_written_onto_the_stored_message()
    {
        var bus = NewBus(new SparkMessagingOptions
        {
            Queues = { ["custom-orders-queue"] = new SparkQueueOptions { MaxAttempts = 9 } },
        });
        var expires = DateTime.UtcNow.AddHours(1);

        await bus.BroadcastAsync(new OrderShipped("orders/1"), new BroadcastOptions
        {
            DeduplicationKey = "k",
            Delay = TimeSpan.FromMinutes(5),
            ExpiresAtUtc = expires,
            ScrubPayloadOnTerminal = true,
        });
        await bus.BroadcastAsync(new OrderShipped("orders/2"), new BroadcastOptions { MaxAttempts = 2 });
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var messages = await session.Query<SparkMessage>().ToListAsync();
        var first = messages.Single(m => m.PayloadJson.Contains("orders/1"));
        first.Id.Should().Be(MessageBus.DeduplicationId(typeof(OrderShipped), "k"), "delay and deduplication combine");
        first.NextAttemptAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
        first.WakeUp.Should().BeFalse();
        first.ExpiresAtUtc!.Value.Should().BeCloseTo(expires, TimeSpan.FromMilliseconds(1));
        first.ScrubPayloadOnTerminal.Should().BeTrue();
        first.MaxAttempts.Should().Be(9, "the queue's MaxAttempts overrides the global one");
        messages.Single(m => m.PayloadJson.Contains("orders/2")).MaxAttempts.Should().Be(2, "the publish's MaxAttempts overrides the queue's");
    }

    [Fact]
    public async Task A_queue_override_must_name_a_declared_queue()
    {
        var bus = NewBus(new SparkMessagingOptions { Queues = { ["mail-bulk"] = new SparkQueueOptions() } });

        await bus.BroadcastAsync(new OrderPlaced("orders/1", 1m), new BroadcastOptions { Queue = "mail-bulk" });
        var undeclared = () => bus.BroadcastAsync(new OrderPlaced("orders/2", 1m), new BroadcastOptions { Queue = "nobody-drains-this" });
        var invalid = () => bus.BroadcastAsync(new OrderPlaced("orders/3", 1m), new BroadcastOptions { Queue = "bad'name" });

        await undeclared.Should().ThrowAsync<InvalidOperationException>();
        await invalid.Should().ThrowAsync<ArgumentException>();
        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        (await session.Query<SparkMessage>().SingleAsync()).QueueName.Should().Be("mail-bulk");
    }

    [Fact]
    public async Task BroadcastOnceAsync_sanitizes_a_key_that_is_not_a_legal_document_id()
    {
        var bus = NewBus();

        // The key comes from a request header in the motivating case, so it cannot be trusted to
        // be a legal RavenDB id.
        await bus.BroadcastOnceAsync(new OrderShipped("orders/1"), "a/b c|d");
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.Id.Should().StartWith("SparkMessages/a_b_c_d.");
    }

    [Fact]
    public async Task DelayBroadcastAsync_sets_NextAttemptAtUtc_to_roughly_now_plus_delay()
    {
        var bus = NewBus();

        await bus.DelayBroadcastAsync(new OrderPlaced("orders/1", 10m), TimeSpan.FromMinutes(5));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.NextAttemptAtUtc.Should().HaveValue();
        message.NextAttemptAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
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
        message.WakeUp.Should().BeFalse();
    }

    [Fact]
    public async Task MaxAttempts_from_options_is_written_onto_the_stored_message()
    {
        var bus = NewBus(new SparkMessagingOptions { MaxAttempts = 12 });

        await bus.BroadcastAsync(new OrderPlaced("orders/1", 10m));
        await Store.WaitForIndexingAsync();

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().SingleAsync();
        message.MaxAttempts.Should().Be(12);
    }

    // ---- EnqueueAsync: publishing inside the caller's transaction (#467, S13; #482, D17) ---------------

    private IMessageOutbox NewOutbox() => (IMessageOutbox)NewBus();

    [Fact]
    public async Task EnqueueAsync_commits_with_the_callers_save_and_not_before()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await NewOutbox().EnqueueAsync(session, new OrderPlaced("orders/2", 1m));
            await session.StoreAsync(new { Name = "the data change" }, "Data/1");

            using (var other = Store.OpenAsyncSession())
                (await other.Query<SparkMessage>().Customize(c => c.WaitForNonStaleResults()).CountAsync()).Should().Be(0, "only stored, not saved");

            await session.SaveChangesAsync();
        }

        using var verify = Store.OpenAsyncSession();
        var message = await verify.Query<SparkMessage>().Customize(c => c.WaitForNonStaleResults()).SingleAsync();
        message.MessageType.Should().Be(typeof(OrderPlaced).AssemblyQualifiedName);
        message.Status.Should().Be(EMessageStatus.Pending);
        verify.Advanced.GetDocumentId(message).Should().StartWith("SparkMessages/");
    }

    [Fact]
    public async Task EnqueueAsync_in_a_session_that_is_never_saved_publishes_nothing()
    {
        using (var session = Store.OpenAsyncSession())
            await NewOutbox().EnqueueAsync(session, new OrderPlaced("orders/3", 1m));

        using var verify = Store.OpenAsyncSession();
        (await verify.Query<SparkMessage>().Customize(c => c.WaitForNonStaleResults()).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_refuses_a_deduplication_key()
    {
        using var session = Store.OpenAsyncSession();
        var act = () => NewOutbox().EnqueueAsync(session, new OrderPlaced("orders/4", 1m), new BroadcastOptions { DeduplicationKey = "k" });

        await act.Should().ThrowAsync<ArgumentException>();
        session.Advanced.UseOptimisticConcurrency.Should().BeFalse("the caller's session is left as it was");
    }
}
