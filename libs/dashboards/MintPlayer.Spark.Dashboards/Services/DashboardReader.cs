using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>One reading of a dashboard: its values (history included) and when they were read.</summary>
internal sealed record DashboardSnapshot(IReadOnlyDictionary<string, object?> Values, DateTime GeneratedAtUtc);

/// <summary>
/// Runs a dashboard's providers. A viewer's reading is shared for <see cref="SparkDashboardsOptions.CacheDuration"/>, so
/// concurrent viewers and the live hub's re-reads cost one run; the sampler's reading is never cached.
/// </summary>
internal sealed class DashboardReader(
    IServiceScopeFactory scopes,
    DashboardStorage storage,
    IOptions<SparkDashboardsOptions> options,
    TimeProvider time,
    ILogger<DashboardReader> logger)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DashboardSnapshot>>> cache = new(StringComparer.Ordinal);

    /// <summary>The values a viewer sees, history included; shared for the cache duration.</summary>
    public Task<DashboardSnapshot> ReadAsync(DashboardDefinition dashboard, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        while (true)
        {
            var entry = cache.GetOrAdd(dashboard.Name, _ => new Lazy<Task<DashboardSnapshot>>(() => ReadFreshAsync(dashboard)));
            var task = entry.Value;
            // A failed reading or an expired one is dropped (only this entry, so a concurrent refresh is kept).
            if (task.IsFaulted || task.IsCanceled
                || (task.IsCompletedSuccessfully && now - task.Result.GeneratedAtUtc >= options.Value.CacheDuration))
            {
                cache.TryRemove(new KeyValuePair<string, Lazy<Task<DashboardSnapshot>>>(dashboard.Name, entry));
                continue;
            }
            return task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Drops every cached reading, so the next viewer reads afresh.</summary>
    public void Invalidate() => cache.Clear();

    /// <summary>The numbers of the dashboard's history values for one sample, read now (never cached).</summary>
    public async Task<IReadOnlyDictionary<string, double>> SampleAsync(DashboardDefinition dashboard, CancellationToken cancellationToken)
    {
        var values = await FillAsync(dashboard, isSampling: true, cancellationToken);
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in dashboard.History)
        {
            if (DashboardValues.SampleOf(values.Get(name)) is { } sample)
                samples[name] = sample;
            else if (values.Get(name) is not null)
                logger.LogWarning("Dashboard {Dashboard}: {Value} keeps a history but is not a number or a meter; not sampled.", dashboard.Name, name);
        }
        return samples;
    }

    private async Task<DashboardSnapshot> ReadFreshAsync(DashboardDefinition dashboard)
    {
        // Not the first viewer's token: the reading is shared, and one viewer leaving must not fail the others.
        var cancellationToken = CancellationToken.None;
        var values = await FillAsync(dashboard, isSampling: false, cancellationToken);
        var result = new Dictionary<string, object?>(values.All, StringComparer.Ordinal);

        if (dashboard.History.Count > 0)
        {
            var o = options.Value;
            var to = time.GetUtcNow().UtcDateTime;
            var from = to - o.HistoryWindow;
            var bucket = o.HistoryWindow / Math.Max(1, o.HistoryPoints);
            foreach (var name in dashboard.History)
            {
                try
                {
                    result[name + "History"] = await storage.ReadHistoryAsync(dashboard.Name, name, from, to, bucket, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A history that cannot be read shows as none; the current values still show.
                    logger.LogWarning(ex, "Dashboard {Dashboard}: the history of {Value} could not be read.", dashboard.Name, name);
                    result[name + "History"] = null;
                }
            }
        }

        return new DashboardSnapshot(result, time.GetUtcNow().UtcDateTime);
    }

    private async Task<DashboardValues> FillAsync(DashboardDefinition dashboard, bool isSampling, CancellationToken cancellationToken)
    {
        var values = new DashboardValues(dashboard.Name, isSampling);
        await using var scope = scopes.CreateAsyncScope();
        foreach (var type in dashboard.Providers)
        {
            try
            {
                var provider = (IDashboardProvider)(scope.ServiceProvider.GetService(type) ?? ActivatorUtilities.CreateInstance(scope.ServiceProvider, type));
                await provider.FillAsync(values, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One failing provider leaves its values empty ("—") rather than the whole page an error; its
                // exception text goes to the log, never into a value (PRD §9.3).
                logger.LogError(ex, "Dashboard {Dashboard}: provider {Provider} failed.", dashboard.Name, type.Name);
            }
        }
        return values;
    }
}
