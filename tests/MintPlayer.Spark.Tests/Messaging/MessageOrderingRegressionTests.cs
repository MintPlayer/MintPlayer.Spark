using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Ordering across the retry path — the guarantee the old implementation advertised and did not keep.
/// </summary>
/// <remarks>
/// <para>
/// Before partitioning, a failing message was written back with <c>Status = Failed</c>, which bumped
/// its change vector and moved it to the <i>back</i> of its subscription's delivery order. The worker
/// then acknowledged the batch and took the next message immediately, so a newer message overtook the
/// retrying one. The observed handler log was
/// <c>[enter:m1, fail:m1, enter:m2, exit:m2, enter:m1, exit:m1]</c>.
/// </para>
/// <para>
/// In production that let <c>FinalizeBuildMessage</c> overtake a failed <c>ParseSessionMessage</c> and
/// close a build on partial data, publishing a wrong coverage percentage — silently.
/// </para>
/// <para>
/// <b>Assertions are ordering assertions over a finished log.</b> Nothing here asserts elapsed time,
/// and nothing asserts that an event is absent within a window: a correct pump cannot emit an
/// out-of-order log at any speed, so these tests cannot flake on a slow machine.
/// </para>
/// </remarks>
public class MessageOrderingRegressionTests : SparkTestDriver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    private const string Lane = "ordering-regression";

    public record OrderedMessage(string Partition, string Key);

    private sealed class RecordingRecipient(string failOnceFor) : IRecipient<OrderedMessage>
    {
        private int failuresIssued;

        public ConcurrentQueue<string> Log { get; } = new();

        public Task HandleAsync(OrderedMessage message, CancellationToken cancellationToken = default)
        {
            Log.Enqueue($"enter:{message.Key}");

            if (message.Key == failOnceFor && Interlocked.Increment(ref failuresIssued) == 1)
            {
                Log.Enqueue($"fail:{message.Key}");
                throw new InvalidOperationException($"transient failure for {message.Key}");
            }

            Log.Enqueue($"exit:{message.Key}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Ordered_lane_does_not_start_the_next_message_while_the_head_is_retrying()
    {
        var recipient = new RecordingRecipient(failOnceFor: "m1");
        await using var host = NewHost(recipient, RetrySchedule.Ladder("1s"));

        var pump = host.StartLane(Lane);

        // Same partition: these two messages depend on each other.
        await host.Bus.BroadcastAsync(new OrderedMessage("build-1", "m1"), Lane);
        await host.Bus.BroadcastAsync(new OrderedMessage("build-1", "m2"), Lane);
        pump.Ring();

        await WaitForAllCompletedAsync(2);

        var log = Log(recipient);
        var m1Exit = log.IndexOf("exit:m1");
        var m2Enter = log.IndexOf("enter:m2");

        m1Exit.Should().BeGreaterThanOrEqualTo(0, "m1 must eventually succeed");
        m2Enter.Should().BeGreaterThanOrEqualTo(0, "m2 must eventually run");
        m2Enter.Should().BeGreaterThan(
            m1Exit,
            $"m2 must not start while m1 is still retrying — observed [{string.Join(", ", log)}]");
    }

    [Fact]
    public async Task A_blocked_partition_does_not_block_a_different_partition()
    {
        // The other half of the bargain: ordering is bought per partition, not per lane. A failing
        // build must not stall an unrelated one, which is what makes a blocking head tolerable.
        var recipient = new RecordingRecipient(failOnceFor: "slow-1");
        await using var host = NewHost(recipient, RetrySchedule.Ladder("2s"));

        var pump = host.StartLane(Lane);

        await host.Bus.BroadcastAsync(new OrderedMessage("build-slow", "slow-1"), Lane);
        await host.Bus.BroadcastAsync(new OrderedMessage("build-slow", "slow-2"), Lane);
        await host.Bus.BroadcastAsync(new OrderedMessage("build-fast", "fast-1"), Lane);
        pump.Ring();

        await WaitForAllCompletedAsync(3);

        var log = Log(recipient);

        // Within the blocked partition, order still holds.
        log.IndexOf("enter:slow-2").Should().BeGreaterThan(
            log.IndexOf("exit:slow-1"),
            $"the slow partition must stay ordered — observed [{string.Join(", ", log)}]");

        // Across partitions it does not: the unrelated build ran while the other was parked.
        log.IndexOf("exit:fast-1").Should().BeLessThan(
            log.IndexOf("enter:slow-2"),
            $"an unrelated partition must not wait for a parked one — observed [{string.Join(", ", log)}]");
    }

    [Fact]
    public async Task Only_the_handlers_that_failed_are_replayed()
    {
        // Several handlers per message type, and a retry re-runs only what failed.
        var good = new CountingRecipient();
        var flaky = new FailsOnceRecipient();

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services =>
            {
                services.AddSingleton<IRecipient<OrderedMessage>>(good);
                services.AddSingleton<IRecipient<OrderedMessage>>(flaky);
            },
            lanes => lanes.Queue(Lane)
                .Ordered()
                .PartitionBy<OrderedMessage>(m => m.Partition)
                .Retry(RetrySchedule.Ladder("1s")));

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new OrderedMessage("build-1", "only"), Lane);
        pump.Ring();

        await WaitForAllCompletedAsync(1);

        good.Calls.Should().Be(1, "a handler that already succeeded must never be invoked again");
        flaky.Calls.Should().Be(2, "the handler that failed must be retried");
    }

    private sealed class CountingRecipient : IRecipient<OrderedMessage>
    {
        private int calls;
        public int Calls => calls;

        public Task HandleAsync(OrderedMessage message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }
    }

    private sealed class FailsOnceRecipient : IRecipient<OrderedMessage>
    {
        private int calls;
        public int Calls => calls;

        public Task HandleAsync(OrderedMessage message, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("first attempt fails");

            return Task.CompletedTask;
        }
    }

    private MessagingTestHost NewHost(RecordingRecipient recipient, IRetrySchedule schedule)
        => new(
            Store,
            TimeProvider.System,
            services => services.AddSingleton<IRecipient<OrderedMessage>>(recipient),
            lanes => lanes.Queue(Lane)
                .Ordered()
                .PartitionBy<OrderedMessage>(m => m.Partition)
                .MaxPartitionsInFlight(4)
                .Retry(schedule));

    private Task WaitForAllCompletedAsync(int expected) => AsyncWait.ForAsync(
        async () =>
        {
            using var session = Store.OpenAsyncSession();
            return await session.Query<SparkMessage>().Where(m => m.QueueName == Lane).ToListAsync();
        },
        messages => messages.Count == expected && messages.All(m => m.Status == EMessageStatus.Completed),
        $"all {expected} messages to reach Completed",
        last => $"[{string.Join(", ", last?.Select(m => m.Status.ToString()) ?? [])}]",
        PollTimeout,
        TimeSpan.FromMilliseconds(100));

    private static List<string> Log(RecordingRecipient recipient) => [.. recipient.Log];
}
