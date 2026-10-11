using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// Samples every value declared with <see cref="SparkDashboardBuilder.History"/> once per
/// <see cref="SparkDashboardsOptions.SampleInterval"/> into its time series (D14, D29).
/// </summary>
/// <remarks>
/// <para>
/// <b>One host per slot.</b> A slot is <c>floor(utcNow / interval)</c>; a host appends only after
/// <see cref="DashboardSamplerLease"/> let it claim that slot, and it appends at the slot's start, so a slot is written
/// once and a repeated write would overwrite rather than add.
/// </para>
/// <para>
/// <b>No policies, no samples.</b> The collection is configured first (retention, rollup, revisions off); if that fails
/// nothing is sampled, because a series without a retention policy grows for ever.
/// </para>
/// </remarks>
internal sealed class DashboardSampler(
    DashboardCatalog catalog,
    DashboardReader reader,
    DashboardStorage storage,
    DashboardSamplerLease lease,
    IHostApplicationLifetime lifetime,
    IOptions<SparkDashboardsOptions> options,
    TimeProvider time,
    ILogger<DashboardSampler> logger) : BackgroundService
{
    /// <summary>The lease lasts three slots: a holder may miss two before a standby takes over.</summary>
    private TimeSpan Ttl => options.Value.SampleInterval * 3;

    /// <summary>The slot <paramref name="utc"/> falls in.</summary>
    public long SlotOf(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).Ticks / (double)options.Value.SampleInterval.Ticks);

    /// <summary>The start of <paramref name="slot"/>: every host writes that slot's points at this timestamp.</summary>
    public DateTime StartOf(long slot) => DateTime.UnixEpoch.AddTicks(slot * options.Value.SampleInterval.Ticks);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Sample || !catalog.All.Any(d => d.History.Count > 0))
            return;

        // After UseSpark() prepared the database: the web host's own hosted service builds the pipeline after this one
        // starts, so the started signal, not StartAsync, is the moment the database surely exists.
        using (var started = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStarted, stoppingToken))
        try
        {
            await Task.Delay(Timeout.Infinite, started.Token);
        }
        catch (OperationCanceledException)
        {
        }
        if (stoppingToken.IsCancellationRequested)
            return;

        try
        {
            await storage.ConfigureAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not configure the {Collection} collection's time-series policies and revisions; dashboard history is not sampled.", DashboardStorage.Collection);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = time.GetUtcNow().UtcDateTime;
                var slot = SlotOf(now);
                try
                {
                    await TrySampleAsync(slot, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Dashboard history: slot {Slot} was not sampled.", slot);
                }

                var wait = StartOf(slot + 1) - time.GetUtcNow().UtcDateTime;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Samples <paramref name="slot"/> when this host can claim it; returns whether it did. Every dashboard with a
    /// history is written at the slot's start.
    /// </summary>
    public async Task<bool> TrySampleAsync(long slot, CancellationToken cancellationToken)
    {
        var dashboards = catalog.All.Where(d => d.History.Count > 0).ToList();
        if (dashboards.Count == 0)
            return false;
        if (!await lease.TryClaimSlotAsync(slot, time.GetUtcNow().UtcDateTime, Ttl, cancellationToken))
            return false;

        var timestamp = StartOf(slot);
        foreach (var dashboard in dashboards)
        {
            var samples = await reader.SampleAsync(dashboard, cancellationToken);
            await storage.AppendAsync(dashboard.Name, timestamp, samples, cancellationToken);
        }
        return true;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await lease.ReleaseAsync(bound.Token);
        }
        catch (Exception ex)
        {
            // The lease lapses at its TTL instead; a standby then takes over a little later.
            logger.LogDebug(ex, "The dashboard sampler lease was not released.");
        }
    }
}
