using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Owns the messaging host's lifecycle: prunes legacy subscriptions, competes for the messaging
/// lease, and while it holds the lease runs either the single feeder plus per-queue pumps
/// (<see cref="ESubscriptionMode.SingleSubscription"/>) or one worker per queue
/// (<see cref="ESubscriptionMode.SubscriptionPerQueue"/>).
/// </summary>
internal sealed partial class MessageSubscriptionManager : BackgroundService
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;
    [Inject] private readonly ILogger<MessageSubscriptionManager> logger;
    [Inject] private readonly ILoggerFactory loggerFactory;
    [Inject] private readonly MessagingLeaseManager leaseManager;
    [Inject] private readonly LegacySubscriptionCleanup legacyCleanup;
    [Inject] private readonly MessageQueueRouter router;
    [Inject] private readonly MessageProcessor processor;

    private readonly List<MessageSubscriptionWorker> perQueueWorkers = new();
    private MessageFeeder? feeder;
    private Task? feederTask;
    private CancellationTokenSource? leaseLifetime;
    private volatile bool isLeader;

    /// <summary>
    /// Cancelled first thing in <see cref="StopAsync"/>, to end the lease loop before the teardown
    /// runs. The host's stopping token only fires in <c>base.StopAsync</c>, after the drain, so on
    /// its own it leaves the loop running through the whole drain: a renewal falling due there saw
    /// <c>isLeader == false</c>, re-acquired its own lease and started a second feeder on a host
    /// that was stopping.
    /// </summary>
    private readonly CancellationTokenSource stopRequested = new();

    /// <summary>Whether this host currently holds the messaging lease. For tests and diagnostics.</summary>
    internal bool IsLeader => isLeader;

    /// <summary>The running feeder, while this host leads in <see cref="ESubscriptionMode.SingleSubscription"/> mode. For spikes and diagnostics.</summary>
    internal MessageFeeder? Feeder => feeder;

    private SparkMessagingOptions Options => options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prune first, and in this method rather than a migration or the middleware registry: the
        // prune and the create that follows it are then straight-line async code with no blocking
        // call and no ordering argument to defend. See LegacySubscriptionCleanup.
        using var loop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, stopRequested.Token);
        var loopToken = loop.Token;

        await legacyCleanup.RunAsync(loopToken);

        // Declared queues count too: a BroadcastOptions.Queue override may only name a declared queue,
        // and in per-queue mode a queue with no worker is a queue nobody drains.
        var queueNames = DiscoverQueueNames(serviceProvider)
            .Union(Options.Queues.Keys.Where(Services.QueueNames.IsValid), StringComparer.Ordinal)
            .ToList();

        if (Options.SubscriptionMode == ESubscriptionMode.SubscriptionPerQueue)
        {
            foreach (var (name, queue) in Options.Queues)
            {
                if (queue.MaxConcurrency > 1)
                    logger.LogWarning(
                        "Queue '{QueueName}' sets MaxConcurrency = {MaxConcurrency}, which only applies in "
                        + "SingleSubscription mode; in SubscriptionPerQueue mode it is handled one message at a time",
                        name, queue.MaxConcurrency);
            }
        }
        if (queueNames.Count == 0)
        {
            logger.LogWarning(
                "No message queues discovered from IRecipient<T> registrations. MessageSubscriptionManager will not start.");
            return;
        }

        logger.LogInformation(
            "MessageSubscriptionManager discovered {Count} queue(s) in {Mode} mode: {Queues}",
            queueNames.Count, Options.SubscriptionMode, string.Join(", ", queueNames));

        // The delays wake on loopToken, so a stop ends the loop at once. The lease requests run on
        // stoppingToken, which StopAsync does not cancel until the loop has exited: an acquire that is
        // in flight when the stop arrives completes, and the check below hands its lease to the
        // teardown's release instead of starting anything.
        while (!loopToken.IsCancellationRequested)
        {
            if (!isLeader)
            {
                if (await leaseManager.TryAcquireAsync(stoppingToken))
                {
                    if (loopToken.IsCancellationRequested)
                        break; // Stopping: StopAsync releases the lease we just took.

                    isLeader = true;
                    leaseManager.IsHeld = true;
                    await StartMessagingAsync(queueNames, stoppingToken);
                }
                else
                {
                    // Standby. Nothing runs here: no feeder, no pumps, no sweeper. The subscription
                    // itself would tolerate a second worker — WaitForFree parks it — but N sweepers
                    // patching the same documents and N hosts deploying the same indexes are worth
                    // avoiding.
                    await SafeDelayAsync(MessagingLeaseManager.StandbyPollInterval, loopToken);
                    continue;
                }
            }

            await SafeDelayAsync(MessagingLeaseManager.RenewInterval, loopToken);
            if (loopToken.IsCancellationRequested)
                break;

            var renewed = await leaseManager.TryRenewAsync(stoppingToken);
            if (loopToken.IsCancellationRequested)
                break; // Stopping: StopAsync tears down and releases; a failed renewal here is not an eviction.

            if (!renewed)
            {
                // Evicted, or the lease lapsed while we were busy. Tear down immediately rather
                // than keep feeding without a lease: the new holder is already starting, and while
                // the server stops us both feeding at once, two hosts running sweepers and index
                // deployments is exactly what the lease exists to prevent.
                logger.LogWarning("Lost the messaging lease; stopping the feeder and pumps");
                isLeader = false;
                leaseManager.IsHeld = false;
                await StopMessagingAsync(stoppingToken);
            }
        }
    }

    private async Task StartMessagingAsync(List<string> queueNames, CancellationToken stoppingToken)
    {
        leaseLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        if (Options.SubscriptionMode == ESubscriptionMode.SingleSubscription)
        {
            router.Start(leaseLifetime.Token);
            feeder = new MessageFeeder(router, processor.Admission, documentStore, options, loggerFactory);
            feederTask = feeder.StartAsync(leaseLifetime.Token);
            await feederTask;
            logger.LogInformation(
                "Messaging started: one subscription '{SubscriptionName}' feeding {Count} in-process queue lane(s)",
                MessageFeeder.SubscriptionNameConstant, queueNames.Count);
            return;
        }

        foreach (var queueName in queueNames)
        {
            var worker = new MessageSubscriptionWorker(
                queueName, documentStore, serviceProvider, options, loggerFactory);
            lock (perQueueWorkers) perQueueWorkers.Add(worker);
            logger.LogInformation("Starting subscription worker for queue '{QueueName}'", queueName);
            await worker.StartAsync(leaseLifetime.Token);
        }
    }

    /// <summary>
    /// Stops feeding, drains the pumps, and only then lets go. The order is the point: releasing the
    /// lease first would let the incoming host start feeding the same queues while this host's pumps
    /// are still running messages from them, which is the one thing that breaks per-queue FIFO.
    /// </summary>
    private async Task StopMessagingAsync(CancellationToken cancellationToken)
    {
        // ⚠ This method has TWO callers that can run at the same time: the lease-lost branch of
        // ExecuteAsync, and StopAsync when the host shuts down. Both used to test a field for null,
        // AWAIT, and then dereference it - so whichever arrived second dereferenced what the first
        // had already nulled, and host shutdown threw NullReferenceException out of the logging
        // pipeline. Each field is therefore taken with Interlocked.Exchange: whoever wins owns the
        // object and disposes it exactly once, and the loser sees null and skips.

        // 1. Stop feeding. Nothing new gets claimed from here on.
        if (Interlocked.Exchange(ref feeder, null) is { } activeFeeder)
        {
            feederTask = null;
            try { await activeFeeder.StopAsync(cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Error stopping the message feeder"); }
            activeFeeder.Dispose();
        }

        // Same hazard, and a List is not safe to enumerate while the other caller clears it.
        MessageSubscriptionWorker[] workers;
        lock (perQueueWorkers)
        {
            workers = [.. perQueueWorkers];
            perQueueWorkers.Clear();
        }

        foreach (var worker in workers)
        {
            try { await worker.StopAsync(cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Error stopping a per-queue subscription worker"); }
            worker.Dispose();
        }

        // 2. Drain in-flight messages. A message still running keeps its claim renewed, so if the
        //    drain times out it is reclaimed by the sweeper rather than lost.
        await router.DrainAsync(Options.ClaimTtl);

        // 3. Now release the lease. Not on cancellationToken: the release bounds itself, and a
        //    cancelled stop token must not cost every standby the TTL. A release that cannot happen
        //    (store disposed or unreachable) is handled inside; what reaches this catch is unexpected.
        try { await leaseManager.ReleaseAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Error releasing the messaging lease"); }

        if (Interlocked.Exchange(ref leaseLifetime, null) is { } lifetime)
        {
            await lifetime.CancelAsync();
            lifetime.Dispose();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MessageSubscriptionManager stopping");

        // Not leading from the moment the stop begins, before any await: the sweeper reads IsHeld,
        // and callers observe IsLeader. Safe ahead of the cancel below, because the loop re-checks
        // loopToken after every lease request before it acts on the answer.
        isLeader = false;
        leaseManager.IsHeld = false;

        // End the lease loop before tearing down, so nothing re-acquires or restarts behind the
        // teardown. It exits at once (its delays wake on stopRequested); an in-flight lease request
        // completes first. Bounded by the caller's token, like base.StopAsync.
        await stopRequested.CancelAsync();
        if (ExecuteTask is { } loop)
            await Task.WhenAny(loop, Task.Delay(Timeout.Infinite, cancellationToken));

        await StopMessagingAsync(cancellationToken);
        await router.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        stopRequested.Dispose();
        base.Dispose();
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>
    /// A queue is worth a lane exactly when its message type has a recipient, which is the question
    /// <see cref="MessageRecipientRegistry"/> exists to answer — so the scan over the registered
    /// <c>IRecipient&lt;T&gt;</c> descriptors lives there rather than being repeated here.
    /// </summary>
    private static IEnumerable<string> DiscoverQueueNames(IServiceProvider serviceProvider)
    {
        var registry = serviceProvider.GetService<MessageRecipientRegistry>();
        if (registry is null)
            return [];

        // Qualified: this type has its own QueueNames member in scope, which would shadow the helper.
        return registry.ConsumedMessageTypes
            .Select(Services.QueueNames.ForMessageType)
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Provides access to the IServiceCollection at runtime for queue discovery.
/// </summary>
internal interface IServiceCollectionAccessor
{
    IServiceCollection Services { get; }
}

internal sealed partial class ServiceCollectionAccessor : IServiceCollectionAccessor
{
    [Inject] public IServiceCollection Services { get; }
}
