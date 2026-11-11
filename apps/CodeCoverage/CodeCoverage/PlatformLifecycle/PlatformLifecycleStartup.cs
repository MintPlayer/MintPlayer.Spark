using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;

namespace CodeCoverage.PlatformLifecycle;

/// <summary>
/// #613 item 1: collects the platform lifecycle once at startup when the stored rows are not in the current shape. A row
/// the lookup refuses fails EVERY dependency lookup in the fleet (<see cref="DependencyPlatformTable"/>), and
/// <see cref="PlatformLifecycleCronJob"/> runs only at its daily slot: Spark Cron has no catch-up and no run-now
/// (libs/cron/MintPlayer.Spark.Cron/README.md "No Catch-Up"). On 2026-11-09 the deploy of #606 left the per-major rows
/// without a channel in place until 04:17 UTC the next day, and 10 of 10 first lookups failed.
/// </summary>
/// <remarks>An empty table is left to the schedule: it fails no lookup (no SDK is offered), and a host without rows,
/// such as a test host, makes no outbound request.</remarks>
public sealed partial class PlatformLifecycleStartup : BackgroundService
{
    [Inject] private readonly IServiceScopeFactory scopes;
    [Inject] private readonly IHostApplicationLifetime lifetime;
    [Inject] private readonly ILogger<PlatformLifecycleStartup> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The document store and the Spark context are ready once the host has started.
        var started = new TaskCompletionSource();
        await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try { await started.Task; }
            catch (OperationCanceledException) { return; }
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using (var session = store.OpenAsyncSession())
            {
                var stored = await session.Advanced.LoadStartingWithAsync<PlatformRelease>(PlatformRelease.Prefix, pageSize: 1024, token: stoppingToken);
                if (!NeedsCollection(stored))
                    return;
            }
            logger.LogInformation("Platform lifecycle: stored rows are not in the current shape; collecting now instead of at the daily slot");
            await scope.ServiceProvider.GetRequiredService<PlatformLifecycleCronJob>().RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Platform lifecycle: the startup collection failed; the daily slot collects next");
        }
    }

    /// <summary>A stored row has a channel the lookup refuses (the per-major rows written before #606 have none).</summary>
    public static bool NeedsCollection(IEnumerable<PlatformRelease> stored) => stored.Any(r => !DependencyPlatformTable.IsChannel(r.Channel));
}
