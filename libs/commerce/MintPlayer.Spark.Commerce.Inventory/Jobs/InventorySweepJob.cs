using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory.Live;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Inventory.Jobs;

/// <summary>
/// Told about each held claim the sweeper released because it lapsed: checkout cancels the provider payment of an
/// expired PaymentPending order (D3), promotions offer the units to the waiting list (D18). Discovered
/// (<c>[SparkDiscoverable]</c>) in an application; a library registers its own (scoped).
/// </summary>
/// <remarks>Called after the release committed, at least once: a crash between the release and the call loses the call, so handlers also reconcile on their own schedule when that matters.</remarks>
[SparkDiscoverable]
public interface IInventoryClaimLapsed
{
    Task OnLapsedAsync(InventoryClaim claim, CancellationToken cancellationToken);
}

/// <summary>
/// The sweeper (§4.3), every few seconds: releases lapsed held claims (lazily, the same way a claim does), tells the
/// <see cref="IInventoryClaimLapsed"/> handlers, and republishes the availability of the quotas it touched. Not needed
/// for correctness (I3): killing it leaves availability stale, never oversold (S1, S3).
/// </summary>
internal sealed class InventorySweepJob(
    InventoryEngine engine,
    InventoryAvailabilityPublisher publisher,
    IOptions<SparkCommerceInventoryOptions> options,
    IServiceProvider services,
    ILogger<InventorySweepJob> logger) : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Inventory:SweepSchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/5 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var released = await engine.ReleaseLapsedAsync(options.Value.SweepBatchSize, cancellationToken);
        if (released.Count == 0)
            return;

        var handlers = services.GetServices<IInventoryClaimLapsed>().ToList();
        foreach (var claim in released)
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.OnLapsedAsync(claim, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Handler} failed for lapsed claim {ClaimId}.", handler.GetType().Name, claim.Id);
                }
            }

        // Each release already published when PublishOnCommit is on; otherwise this is the only publication.
        if (!options.Value.PublishOnCommit)
            await publisher.PublishAsync(engine, released.SelectMany(c => c.Allocations).Select(a => a.QuotaId).Distinct().ToList(), cancellationToken);
        logger.LogDebug("Inventory sweep released {Count} lapsed claims.", released.Count);
    }
}
