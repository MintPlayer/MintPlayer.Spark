using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// The four properties the messaging subsystem is supposed to have and which <b>no test covered</b>
/// before the single-subscription rework: per-queue FIFO, isolation between queues, recovery of a
/// message abandoned mid-handler, and single-consumer exclusivity.
/// <para>
/// Each asserts the positive — that the thing happens — never merely that it does not happen early.
/// This repository has already shipped retry tests that asserted a negative and stayed green while
/// delivery was entirely dead, which is how five production queues went unnoticed for months.
/// </para>
/// </summary>
public class MessagingInvariantsTests : SparkTestDriver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    // --- Messages, one per queue ---------------------------------------------

    [MessageQueue("invariants-ordered")]
    public sealed class OrderedMessage { public int Sequence { get; set; } }

    [MessageQueue("invariants-slow")]
    public sealed class SlowMessage { public string? Id { get; set; } }

    [MessageQueue("invariants-fast")]
    public sealed class FastMessage { public string? Id { get; set; } }

    // --- Recipients -----------------------------------------------------------

    public sealed class OrderedRecipient : IRecipient<OrderedMessage>
    {
        public ConcurrentQueue<int> Seen { get; } = new();

        public async Task HandleAsync(OrderedMessage message, CancellationToken cancellationToken = default)
        {
            // A real gap between messages: without a delay, "in order" could hold by luck even if
            // several were dispatched concurrently.
            await Task.Delay(150, cancellationToken);
            Seen.Enqueue(message.Sequence);
        }
    }

    public sealed class SlowRecipient : IRecipient<SlowMessage>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(SlowMessage message, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
        }
    }

    public sealed class FastRecipient : IRecipient<FastMessage>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(FastMessage message, CancellationToken cancellationToken = default)
        {
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    // --- Invariant 1: FIFO within a queue ------------------------------------

    [Fact]
    public async Task Messages_on_one_queue_complete_in_the_order_they_were_published()
    {
        var recipient = new OrderedRecipient();
        var services = NewServices();
        services.AddSingleton<IRecipient<OrderedMessage>>(recipient);
        await using var provider = services.BuildServiceProvider();

        var bus = new MessageBus(Store, Options.Create(new SparkMessagingOptions()));
        for (var i = 1; i <= 5; i++)
            await bus.BroadcastAsync(new OrderedMessage { Sequence = i });

        await using var host = await StartManagerAsync(provider);

        await AsyncWait.UntilAsync(
            () => recipient.Seen.Count == 5,
            "all five ordered messages to be handled",
            PollTimeout);

        // One queue is one FIFO lane drained by a single pump with one message in flight.
        recipient.Seen.Should().Equal([1, 2, 3, 4, 5]);
    }

    // --- Invariant 2: isolation between queues -------------------------------

    [Fact]
    public async Task A_blocked_handler_on_one_queue_does_not_delay_another_queue()
    {
        var slow = new SlowRecipient();
        var fast = new FastRecipient();
        var services = NewServices();
        services.AddSingleton<IRecipient<SlowMessage>>(slow);
        services.AddSingleton<IRecipient<FastMessage>>(fast);
        await using var provider = services.BuildServiceProvider();

        var bus = new MessageBus(Store, Options.Create(new SparkMessagingOptions()));
        await bus.BroadcastAsync(new SlowMessage { Id = "slow/1" });

        await using var host = await StartManagerAsync(provider);

        // Wait until the slow handler is genuinely mid-flight before publishing the fast message,
        // so the test cannot pass by the fast one simply being processed first.
        await slow.Started.Task.WaitAsync(PollTimeout);

        await bus.BroadcastAsync(new FastMessage { Id = "fast/1" });

        // This is the requirement in one line: a large job must not hinder a small one.
        await fast.Completed.Task.WaitAsync(PollTimeout);

        slow.Release.TrySetResult();
    }

    // --- Invariant 3: a message abandoned mid-handler comes back --------------

    [Fact]
    public async Task A_message_abandoned_at_Processing_is_reclaimed_and_then_completes()
    {
        // The shape a killed host leaves behind: claimed by a node that no longer exists, with an
        // expiry already in the past. Before the rework this document matched no query at all —
        // not the subscription's, not the sweeper's — so it stayed here for ever and the work was
        // silently lost. For webhook traffic that meant an accepted delivery was dropped.
        string messageId;
        using (var session = Store.OpenAsyncSession())
        {
            var stranded = new SparkMessage
            {
                QueueName = "invariants-fast",
                MessageType = typeof(FastMessage).AssemblyQualifiedName!,
                PayloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(new FastMessage { Id = "fast/stranded" }),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                MaxAttempts = 5,
                Status = EMessageStatus.Processing,
                OwnerId = "some-host-that-died/deadbeef",
                ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
                AttemptCount = 1,
            };
            await session.StoreAsync(stranded);
            await session.SaveChangesAsync();
            messageId = stranded.Id!;
        }
        await Store.WaitForIndexingAsync();

        var sweeper = new MessageRetrySweeper(
            Store,
            Options.Create(new SparkMessagingOptions()),
            NullLogger<MessageRetrySweeper>.Instance,
            new MessagingLeaseManager(Store, NullLogger<MessagingLeaseManager>.Instance) { IsHeld = true });

        var reclaimed = await sweeper.ReclaimAbandonedAsync(CancellationToken.None);
        reclaimed.Should().Be(1, "the expired claim marks the message as abandoned");

        using (var session = Store.OpenAsyncSession())
        {
            var after = await session.LoadAsync<SparkMessage>(messageId);
            after.Status.Should().Be(EMessageStatus.Pending);
            after.OwnerId.Should().BeNull();
            after.ClaimExpiresAtUtc.Should().NotHaveValue();
            after.AttemptCount.Should().Be(2,
                "the abandoned pickup counts against the retry budget, so a message that reliably "
                + "kills its host is eventually dead-lettered rather than crash-looping for ever");
        }

        // And it must actually be delivered again — the positive assertion.
        var fast = new FastRecipient();
        var services = NewServices();
        services.AddSingleton<IRecipient<FastMessage>>(fast);
        await using var provider = services.BuildServiceProvider();
        await using var host = await StartManagerAsync(provider);

        await fast.Completed.Task.WaitAsync(PollTimeout);

        var final = await AsyncWait.ForAsync(
            async () =>
            {
                using var session = Store.OpenAsyncSession();
                return await session.LoadAsync<SparkMessage>(messageId);
            },
            m => m?.Status == EMessageStatus.Completed,
            "the reclaimed message to reach Completed",
            m => $"Status={m?.Status}",
            PollTimeout,
            TimeSpan.FromMilliseconds(100));

        final.Status.Should().Be(EMessageStatus.Completed);
    }

    // --- Invariant 4: single-consumer exclusivity ----------------------------

    [Fact]
    public async Task Two_hosts_against_one_subscription_process_each_message_once()
    {
        var recipientA = new OrderedRecipient();
        var recipientB = new OrderedRecipient();

        var servicesA = NewServices();
        servicesA.AddSingleton<IRecipient<OrderedMessage>>(recipientA);
        await using var providerA = servicesA.BuildServiceProvider();

        var servicesB = NewServices();
        servicesB.AddSingleton<IRecipient<OrderedMessage>>(recipientB);
        await using var providerB = servicesB.BuildServiceProvider();

        var bus = new MessageBus(Store, Options.Create(new SparkMessagingOptions()));
        for (var i = 1; i <= 3; i++)
            await bus.BroadcastAsync(new OrderedMessage { Sequence = i });

        // Both hosts start. Only one can hold the lease, and even if both somehow fed, RavenDB
        // permits a single connected worker per subscription — WaitForFree parks the other rather
        // than throwing SubscriptionInUseException, which is why unconditional WaitForFree matters.
        await using var hostA = await StartManagerAsync(providerA);
        await using var hostB = await StartManagerAsync(providerB);

        await AsyncWait.UntilAsync(
            () => recipientA.Seen.Count + recipientB.Seen.Count == 3,
            "all three messages to be handled exactly once across both hosts",
            PollTimeout);

        // Give a straggler a chance to double-handle, then assert the total is still three.
        await Task.Delay(2000);

        (recipientA.Seen.Count + recipientB.Seen.Count).Should().Be(3,
            "each message is claimed under optimistic concurrency, so exactly one host runs it");

        var everySequence = recipientA.Seen.Concat(recipientB.Seen).OrderBy(x => x).ToList();
        everySequence.Should().Equal([1, 2, 3]);
    }

    // --- Harness --------------------------------------------------------------

    private IServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging(o =>
        {
            o.FallbackPollInterval = TimeSpan.FromSeconds(1);
            o.ClaimTtl = TimeSpan.FromMinutes(2);
            o.ClaimRenewInterval = TimeSpan.FromSeconds(20);
        });
        return services;
    }

    private static async Task<HostedHandle> StartManagerAsync(IServiceProvider provider)
    {
        var manager = provider.GetServices<IHostedService>().OfType<MessageSubscriptionManager>().Single();
        var sweeper = provider.GetServices<IHostedService>().OfType<MessageRetrySweeper>().Single();
        await manager.StartAsync(CancellationToken.None);
        await sweeper.StartAsync(CancellationToken.None);
        return new HostedHandle(manager, sweeper);
    }

    private sealed class HostedHandle(MessageSubscriptionManager manager, MessageRetrySweeper sweeper) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await sweeper.StopAsync(CancellationToken.None); } catch { /* teardown */ }
            try { await manager.StopAsync(CancellationToken.None); } catch { /* teardown */ }
        }
    }
}
