using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// Pushes every dashboard to its viewers every <see cref="SparkDashboardsOptions.RefreshInterval"/> (D12): a dashboard
/// has no document whose change the live hub could see, so this is its change source. A notification is only a dirty
/// bit; each subscriber re-reads under its own rights, and a viewer without them gets no frame.
/// </summary>
internal sealed class DashboardNotifier(
    DashboardCatalog catalog,
    ISparkLiveNotifier notifier,
    IOptions<SparkDashboardsOptions> options,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ids = catalog.All.Select(d => d.ObjectId).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return;

        using var timer = new PeriodicTimer(options.Value.RefreshInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var id in ids)
                    notifier.Changed(id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
