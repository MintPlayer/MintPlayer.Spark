using System.Collections.Concurrent;
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
/// In-queue ordering across the retry path.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests fail on the pre-partitioning implementation, deliberately.</b> They encode the
/// guarantee <c>docs/prd/PRD-SubscriptionWorker.md</c> §8.2 claims ("a failed message blocks its
/// queue until resolved or dead-lettered") and that <c>docs/code-coverage/upload-api.md</c> promises
/// to API consumers ("Finishing is queued behind the parses that preceded it, so it can never close
/// a build on a half-computed number"). Neither holds today.
/// </para>
/// <para>
/// Why it does not hold: when M1's handler fails, the worker writes
/// <c>Status = Failed, NextAttemptAtUtc = …</c> and saves. That save <b>bumps M1's change vector</b>,
/// which moves M1 to the back of the subscription's delivery order. The worker then acknowledges the
/// batch and takes M2 immediately. M1 returns only when <see cref="MessageRetrySweeper"/> sets
/// <c>WakeUp</c> — after M2, and after anything else broadcast in the interval. Per-queue
/// subscriptions bought serialization, not ordering.
/// </para>
/// <para>
/// <b>The assertion is an ordering one, never a timing one.</b> The recorder captures a total order
/// of handler entries and exits, and the test inspects it only after every message has reached a
/// terminal state — a positive, awaited signal. Nothing asserts elapsed time, and nothing asserts
/// the absence of an event within a window (see <see cref="AsyncWait"/>'s doctrine: a timeout is a
/// failure bound, never a success criterion).
/// </para>
/// </remarks>
public class MessageOrderingRegressionTests : SparkTestDriver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    private const string Queue = "ordering-regression";

    public record OrderedMessage(string Key);

    /// <summary>
    /// Records handler entries/exits in the order they happen, and fails the message identified by
    /// <paramref name="failOnceFor"/> the first time it is seen.
    /// </summary>
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
    public async Task Ordered_queue_does_not_start_M2_while_M1_is_retrying()
    {
        var recipient = new RecordingRecipient(failOnceFor: "m1");
        var serviceProvider = ProviderFor(recipient);

        // Broadcast through the real producer path: CreatedAtUtc and the document id are the
        // ordering key the pump must reproduce, so hand-storing documents would test nothing.
        var options = new SparkMessagingOptions
        {
            MaxAttempts = 5,
            // Long enough that an overtake is unmistakable rather than a race, short enough that
            // the test finishes. A correct implementation blocks the queue for this long; today's
            // implementation runs M2 within milliseconds of M1's failure.
            BackoffDelays = [TimeSpan.FromSeconds(3)],
            FallbackPollInterval = TimeSpan.FromSeconds(1),
        };

        var bus = new MessageBus(Store, Options.Create(options), TimeProvider.System, new MessageSequence(TimeProvider.System));
        await bus.BroadcastAsync(new OrderedMessage("m1"), Queue);
        await bus.BroadcastAsync(new OrderedMessage("m2"), Queue);
        await Store.WaitForIndexingAsync();

        var worker = new MessageSubscriptionWorker(
            Queue, Store, serviceProvider, Options.Create(options), NullLoggerFactory.Instance);
        var sweeper = new MessageRetrySweeper(
            Store, Options.Create(options), NullLogger<MessageRetrySweeper>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.ForAsync(
                async () =>
                {
                    using var session = Store.OpenAsyncSession();
                    return await session.Query<SparkMessage>()
                        .Where(m => m.QueueName == Queue)
                        .ToListAsync();
                },
                messages => messages.Count == 2 && messages.All(m => m.Status == EMessageStatus.Completed),
                "both messages to reach Completed",
                last => $"[{string.Join(", ", last?.Select(m => $"{m.Status}") ?? [])}]",
                PollTimeout,
                TimeSpan.FromMilliseconds(100));

            var log = Log(recipient);

            // The guarantee: m1 is finished before m2 is even started.
            var m1Exit = log.IndexOf("exit:m1");
            var m2Enter = log.IndexOf("enter:m2");

            m1Exit.Should().BeGreaterThanOrEqualTo(0, "m1 must eventually succeed");
            m2Enter.Should().BeGreaterThanOrEqualTo(0, "m2 must eventually run");
            m2Enter.Should().BeGreaterThan(
                m1Exit,
                "a queue that guarantees order must not start m2 while m1 is still retrying — "
                + $"observed [{string.Join(", ", log)}]");
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static List<string> Log(RecordingRecipient recipient) => [.. recipient.Log];

    private static IServiceProvider ProviderFor(RecordingRecipient recipient)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRecipient<OrderedMessage>>(recipient);
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        return services.BuildServiceProvider();
    }
}
