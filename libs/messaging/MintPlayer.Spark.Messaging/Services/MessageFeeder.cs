using System.Collections.Concurrent;
using MintPlayer.Spark.Messaging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The one RavenDB subscription the messaging system uses, for any number of lanes.
/// </summary>
/// <remarks>
/// <para>
/// RavenDB allows three data subscriptions per database on both the unlicensed and Community tiers
/// (fifteen per cluster), and concurrent subscription workers are licensed features neither tier
/// includes. One subscription per queue therefore does not scale past three queues — and when the
/// limit is hit the failure is silent, which is how three of a production app's six queues sat dead
/// for months. So: one subscription, one worker, any number of lanes.
/// </para>
/// <para>
/// <b>This class does no handler work.</b> RavenDB does not fetch the next batch until the callback
/// returns, so doing real work here would make every lane wait on every other — measured, a four
/// second handler delayed an unrelated lane by 3.3 seconds. All it does is ring the doorbell of the
/// lane a message belongs to; the lane's pump decides what to run and when.
/// </para>
/// <para>
/// It follows that the subscription <i>over-delivers</i>: it cannot evaluate time, so it hands over a
/// message whose retry is an hour away about as fast as any other. That is harmless precisely because
/// delivery does not decide what runs — the pump's query does. Never "optimize" by dispatching
/// straight from a batch.
/// </para>
/// </remarks>
internal sealed class MessageFeeder : SparkSubscriptionWorker<SparkMessage>
{
    private readonly ILaneRegistry lanes;
    private readonly MessageProcessor processor;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly IReadOnlyCollection<string> knownLaneNames;
    private readonly IOptions<SparkMessagingOptions> options;

    private readonly ConcurrentDictionary<string, MessageLanePump> pumps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The running loops, so a stop can wait for them rather than merely asking them to end.</summary>
    private readonly ConcurrentDictionary<string, Task> pumpTasks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upper bound on how long shutdown waits for the lanes.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? pumpCancellation;

    protected override string SubscriptionName => "SparkMessaging";

    /// <summary>
    /// Ringing a doorbell is cheap, so take a real batch. The old value of 1 existed because the
    /// worker did handler work inline; it no longer does.
    /// </summary>
    protected override int MaxDocsPerBatch => 256;

    public MessageFeeder(
        IDocumentStore store,
        ILaneRegistry lanes,
        MessageProcessor processor,
        IMessageLaneDiscovery discovery,
        IOptions<SparkMessagingOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
        : base(loggerFactory, store)
    {
        this.lanes = lanes;
        this.options = options;
        this.processor = processor;
        this.timeProvider = timeProvider;
        this.loggerFactory = loggerFactory;
        knownLaneNames = discovery.DiscoverLaneNames();
    }

    protected override SubscriptionCreationOptions ConfigureSubscription() => new()
    {
        Name = SubscriptionName,
        // No queue clause, and no WakeUp gate. The old query needed a boolean because a subscription
        // where-clause cannot evaluate now(), so "the backoff has elapsed" had to be materialized as
        // field state by a sweeper. The pump uses an ordinary index query, which can compare times,
        // so the sweeper and its two fields are gone.
        Query = $"from SparkMessages where Status = '{nameof(EMessageStatus.Pending)}' or Status = '{nameof(EMessageStatus.Failed)}'",
    };

    protected override Task OnWorkerStartedAsync()
    {
        pumpCancellation = new CancellationTokenSource();

        // Start every known lane up front rather than on first delivery: a lane whose backlog is all
        // parked or all pre-existing would otherwise never be drained, because nothing new arrives to
        // ring its bell.
        foreach (var laneName in knownLaneNames.Concat(lanes.DeclaredLanes).Distinct(StringComparer.OrdinalIgnoreCase))
            PumpFor(laneName).Ring();

        return Task.CompletedTask;
    }

    protected override async Task OnWorkerStoppedAsync()
    {
        pumpCancellation?.Cancel();

        // Wait for the lanes to actually stop. Cancelling only ASKS them to; a pump may be mid-drain
        // and its handlers mid-message, both holding sessions against the document store. Returning
        // without waiting means the host disposes that store underneath them -- which in a test run
        // appears as the database being deleted while queries are still arriving, and in production
        // as work abandoned part-way with no record of why.
        var running = pumpTasks.Values.ToArray();
        if (running.Length == 0)
            return;

        try
        {
            await Task.WhenAll(running).WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning(
                "{Count} messaging lane(s) had not stopped after {Timeout}; continuing shutdown",
                running.Length, ShutdownTimeout);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "A messaging lane faulted while stopping");
        }
    }

    protected override Task ProcessBatchAsync(SubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        // Distinct: a batch of 200 messages for one lane is one ring, not two hundred.
        foreach (var laneName in batch.Items.Select(i => i.Result.QueueName).Distinct(StringComparer.OrdinalIgnoreCase))
            PumpFor(laneName).Ring();

        return Task.CompletedTask;
    }

    private MessageLanePump PumpFor(string laneName) => pumps.GetOrAdd(laneName, name =>
    {
        var pump = new MessageLanePump(
            lanes.PlanFor(name),
            DocumentStore,
            processor,
            timeProvider,
            loggerFactory.CreateLogger($"MintPlayer.Spark.Messaging.Lane.{name}"));

        pumpTasks[name] = Task.Run(
            () => pump.RunAsync(pumpCancellation?.Token ?? CancellationToken.None), CancellationToken.None);

        return pump;
    });
}

/// <summary>
/// Supplies the lane names that exist because a recipient is registered for a message type.
/// </summary>
internal interface IMessageLaneDiscovery
{
    IReadOnlyCollection<string> DiscoverLaneNames();
}
