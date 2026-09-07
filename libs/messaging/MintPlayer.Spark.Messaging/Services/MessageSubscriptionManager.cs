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

    private readonly List<MessageSubscriptionWorker> perQueueWorkers = new();
    private MessageFeeder? feeder;
    private Task? feederTask;
    private CancellationTokenSource? leaseLifetime;
    private volatile bool isLeader;

    /// <summary>Whether this host currently holds the messaging lease. For tests and diagnostics.</summary>
    internal bool IsLeader => isLeader;

    private SparkMessagingOptions Options => options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prune first, and in this method rather than a migration or the middleware registry: the
        // prune and the create that follows it are then straight-line async code with no blocking
        // call and no ordering argument to defend. See LegacySubscriptionCleanup.
        await legacyCleanup.RunAsync(stoppingToken);

        var queueNames = DiscoverQueueNames(serviceProvider).ToList();
        if (queueNames.Count == 0)
        {
            logger.LogWarning(
                "No message queues discovered from IRecipient<T> registrations. MessageSubscriptionManager will not start.");
            return;
        }

        logger.LogInformation(
            "MessageSubscriptionManager discovered {Count} queue(s) in {Mode} mode: {Queues}",
            queueNames.Count, Options.SubscriptionMode, string.Join(", ", queueNames));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!isLeader)
            {
                if (await leaseManager.TryAcquireAsync(stoppingToken))
                {
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
                    await SafeDelayAsync(MessagingLeaseManager.StandbyPollInterval, stoppingToken);
                    continue;
                }
            }

            await SafeDelayAsync(MessagingLeaseManager.RenewInterval, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                break;

            if (!await leaseManager.TryRenewAsync(stoppingToken))
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
            feeder = new MessageFeeder(router, documentStore, options, loggerFactory);
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
            perQueueWorkers.Add(worker);
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
        // 1. Stop feeding. Nothing new gets claimed from here on.
        if (feeder is not null)
        {
            try { await feeder.StopAsync(cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Error stopping the message feeder"); }
            feeder.Dispose();
            feeder = null;
            feederTask = null;
        }

        foreach (var worker in perQueueWorkers)
        {
            try { await worker.StopAsync(cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Error stopping a per-queue subscription worker"); }
            worker.Dispose();
        }
        perQueueWorkers.Clear();

        // 2. Drain in-flight messages. A message still running keeps its claim renewed, so if the
        //    drain times out it is reclaimed by the sweeper rather than lost.
        await router.DrainAsync(Options.ClaimTtl);

        // 3. Now release the lease.
        try { await leaseManager.ReleaseAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Error releasing the messaging lease"); }

        if (leaseLifetime is not null)
        {
            await leaseLifetime.CancelAsync();
            leaseLifetime.Dispose();
            leaseLifetime = null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MessageSubscriptionManager stopping");

        isLeader = false;
        leaseManager.IsHeld = false;
        await StopMessagingAsync(cancellationToken);
        await router.DisposeAsync();
        await base.StopAsync(cancellationToken);
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
