using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Fans claimed messages out to one in-process FIFO lane per queue name, and runs a pump per lane.
/// <para>
/// This is what replaces one-RavenDB-subscription-per-queue. A single subscription delivers every
/// queue's messages to <see cref="MessageFeeder"/>, which claims each one and hands it here; the
/// per-queue lanes restore the two properties the old design got from the server:
/// </para>
/// <list type="bullet">
/// <item><b>FIFO within a queue</b> — one lane is one channel drained by exactly one pump with at
/// most one message in flight, so message order within a queue is the order the feeder saw, which
/// is document etag order.</item>
/// <item><b>Isolation between queues</b> — lanes are independent tasks, so a handler blocking for
/// ten minutes on the report-parsing queue does not delay a card move on another queue. This was
/// the explicit requirement: large jobs must not hinder small ones.</item>
/// </list>
/// <para>
/// What it does <i>not</i> reproduce is server-side isolation: every queue's documents now travel
/// through one subscription and one feeder. <see cref="ESubscriptionMode.SubscriptionPerQueue"/>
/// remains available for deployments that want that back and have the licence headroom.
/// </para>
/// </summary>
internal sealed partial class MessageQueueRouter : IAsyncDisposable
{
    /// <summary>
    /// Claimed-but-unprocessed messages held per lane. Only ids travel, so the memory cost is
    /// small; the bound exists to stop an unbounded backlog of claims whose TTLs are all ticking.
    /// Reaching it applies backpressure to the feeder, which is the correct response — the claims
    /// already taken are more urgent than accepting more.
    /// </summary>
    private const int LaneCapacity = 512;

    [Inject] private readonly MessageProcessor processor;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> optionsAccessor;
    [Inject] private readonly ILogger<MessageQueueRouter> logger;

    // Lazy, not Lane, and the difference matters: ConcurrentDictionary.GetOrAdd may invoke its
    // factory more than once under contention and discard the losers. The factory here *starts a
    // pump task*, so a discarded lane would leak a task reading a channel nothing ever writes to.
    // Lazy<T> with ExecutionAndPublication guarantees the pump is created exactly once per queue.
    private readonly ConcurrentDictionary<string, Lazy<Lane>> lanes = new(StringComparer.Ordinal);
    private CancellationTokenSource? lifetime;

    private SparkMessagingOptions Options => optionsAccessor.Value;

    /// <summary>Number of live lanes. For tests and diagnostics.</summary>
    internal int LaneCount => lanes.Count;

    public void Start(CancellationToken stoppingToken)
        => lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

    /// <summary>
    /// Routes a claimed message to its queue's lane, creating the lane on first use. Completes when
    /// the message has been accepted into the lane, not when it has been processed — awaiting it
    /// applies the feeder backpressure described on <see cref="LaneCapacity"/>.
    /// </summary>
    public async ValueTask RouteAsync(string queueName, string messageId, CancellationToken cancellationToken)
    {
        var lane = lanes.GetOrAdd(
            queueName,
            static (name, self) => new Lazy<Lane>(
                () => self.CreateLane(name), LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;

        await lane.Channel.Writer.WriteAsync(messageId, cancellationToken);
    }

    private Lane CreateLane(string queueName)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(LaneCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        logger.LogInformation("Started message pump for queue '{QueueName}'", queueName);
        var lane = new Lane(channel);
        lane.PumpTask = PumpAsync(queueName, channel, lifetime?.Token ?? CancellationToken.None);
        return lane;
    }

    /// <summary>Number of lanes whose pump has actually been started. For tests and diagnostics.</summary>
    internal int StartedLaneCount => lanes.Values.Count(l => l.IsValueCreated);

    /// <summary>
    /// Drains one lane, strictly serially. One message in flight at a time is what makes a queue
    /// FIFO; parallelism across queues comes from there being one of these per queue, not from
    /// running several of them per queue.
    /// </summary>
    private async Task PumpAsync(string queueName, Channel<string> channel, CancellationToken cancellationToken)
    {
        await foreach (var messageId in channel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessWithClaimRenewalAsync(messageId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down. The message stays claimed and is reclaimed by the sweeper once
                // its TTL lapses, so it is not lost.
                logger.LogInformation(
                    "Pump for queue '{QueueName}' cancelled while processing {MessageId}; it will be reclaimed",
                    queueName, messageId);
                break;
            }
            catch (Exception ex)
            {
                // MessageProcessor already parks a message whose handlers threw, so reaching here
                // means the failure was in the claim/renewal plumbing itself. Never let it kill the
                // pump: one poisoned message must not take a whole queue down with it.
                logger.LogError(ex, "Pump for queue '{QueueName}' failed on message {MessageId}", queueName, messageId);
            }
        }

        logger.LogInformation("Message pump for queue '{QueueName}' stopped", queueName);
    }

    /// <summary>
    /// Runs the processor while keeping the claim alive, so a handler that legitimately takes
    /// longer than <see cref="SparkMessagingOptions.ClaimTtl"/> is not reclaimed underneath itself.
    /// </summary>
    private async Task ProcessWithClaimRenewalAsync(string messageId, CancellationToken cancellationToken)
    {
        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewUntilDoneAsync(messageId, renewalCts.Token);

        try
        {
            await processor.ProcessAsync(messageId, MessageClaims.NodeId, cancellationToken);
        }
        finally
        {
            await renewalCts.CancelAsync();
            try { await renewal; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    private async Task RenewUntilDoneAsync(string messageId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Options.ClaimRenewInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var stillOurs = await MessageClaims.TryRenewAsync(
                documentStore, messageId, MessageClaims.NodeId, Options.ClaimTtl, cancellationToken);

            if (!stillOurs)
            {
                // The claim was reclaimed while we were working. We cannot un-run the handlers
                // already invoked, but we can say so loudly: this is the window in which a message
                // can be processed twice, and it means ClaimTtl is too short for this handler.
                logger.LogWarning(
                    "Lost the claim on message {MessageId} while still processing it — it has been "
                    + "requeued and may be handled twice. Increase SparkMessagingOptions.ClaimTtl.",
                    messageId);
                return;
            }
        }
    }

    /// <summary>
    /// Stops accepting work and waits for in-flight messages to finish, up to
    /// <paramref name="timeout"/>. Called before the lease is released, so the incoming host cannot
    /// start the same queues while this one is still draining them.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        // Only lanes already created — never .Value on an unrealized Lazy, which would start a pump
        // during shutdown.
        var live = lanes.Values.Where(l => l.IsValueCreated).Select(l => l.Value).ToArray();

        foreach (var lane in live)
            lane.Channel.Writer.TryComplete();

        var pumps = live.Select(l => l.PumpTask).OfType<Task>().ToArray();
        if (pumps.Length == 0)
            return;

        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(timeout));

        if (!pumps.All(p => p.IsCompleted))
        {
            logger.LogWarning(
                "Message pumps did not drain within {Timeout}; remaining messages stay claimed and "
                + "will be reclaimed after SparkMessagingOptions.ClaimTtl", timeout);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetime is not null)
        {
            await lifetime.CancelAsync();
            lifetime.Dispose();
            lifetime = null;
        }

        lanes.Clear();
    }

    private sealed class Lane(Channel<string> channel)
    {
        public Channel<string> Channel { get; } = channel;
        public Task? PumpTask { get; set; }
    }
}
