using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// One series' sample of one slot, or its rollup of one <see cref="SparkDashboardsOptions.RollupInterval"/>, on a store
/// without time series (#536 D45). The id is <c>sparkdashboardsamples/{dashboard}/{series}/{raw|rollup}/{at}</c>, lower
/// case, so a slot written twice is the same row.
/// </summary>
[IdKind(SparkIdKind.Natural)]
internal sealed partial class SparkDashboardSample
{
    public Id<SparkDashboardSample> Id { get; set; }

    /// <summary>The dashboard's name, as the catalog spells it.</summary>
    public string Dashboard { get; set; } = "";

    /// <summary>The value's attribute name.</summary>
    public string Series { get; set; } = "";

    /// <summary>A rollup of every sample from <see cref="At"/> for one rollup interval; else one raw sample.</summary>
    public bool Rollup { get; set; }

    /// <summary>The start of the slot or of the rollup interval, in epoch milliseconds (UTC): no time zone to lose.</summary>
    public long At { get; set; }

    /// <summary>The value (raw), or the sum of the values rolled up.</summary>
    public double Sum { get; set; }

    /// <summary>1 (raw), or how many samples the rollup holds.</summary>
    public long Count { get; set; }

    /// <summary>The last slot a rollup counted, in epoch milliseconds: a slot written twice is counted once.</summary>
    public long LastAt { get; set; }
}

/// <summary>
/// The dashboards' history on a store without time series (#536 D45): SQL Server, PostgreSQL, memory. Each sample is
/// a raw row that expires after <see cref="SparkDashboardsOptions.RawRetention"/>, and is added to the row of its
/// rollup interval, which expires after <see cref="SparkDashboardsOptions.RollupRetention"/>: RavenDB's two time-series
/// policies, kept by hand. A read uses the raw samples for buckets finer than the rollup interval, else the rollups.
/// </summary>
/// <remarks>
/// <para>
/// One writer per slot, under <see cref="DashboardSamplerLease"/>, so the rollup's read-modify-write does not race.
/// </para>
/// <para>
/// Never used on RavenDB, so its query of the collection builds no auto index (D42). The store's expiration sweep
/// deletes the retired rows where it runs; memory has none, and resets on restart.
/// </para>
/// </remarks>
internal sealed class StoreDashboardHistory(ISparkStore store, IOptions<SparkDashboardsOptions> options) : IDashboardHistory
{
    public Task<IReadOnlyList<string>> ConfigureAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task AppendAsync(string dashboard, DateTime timestamp, IReadOnlyDictionary<string, double> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
            return;

        var o = options.Value;
        var at = Milliseconds(timestamp);
        var interval = Math.Max(1, (long)o.RollupInterval.TotalMilliseconds);
        var rollupAt = Floor(at, interval);
        var rawExpiry = DateTimeOffset.FromUnixTimeMilliseconds(at) + o.RawRetention;
        var rollupExpiry = DateTimeOffset.FromUnixTimeMilliseconds(rollupAt + interval) + o.RollupRetention;

        using var session = store.OpenSession();
        var ids = samples.Keys.ToDictionary(s => s, s => (Raw: IdOf(dashboard, s, false, at), Rollup: IdOf(dashboard, s, true, rollupAt)), StringComparer.Ordinal);
        var existing = await session.LoadAsync<SparkDashboardSample>(ids.Values.SelectMany(i => new[] { i.Raw, i.Rollup }), cancellationToken);

        foreach (var (series, value) in samples)
        {
            var raw = existing.GetValueOrDefault(ids[series].Raw);
            if (raw is null)
            {
                raw = new SparkDashboardSample { Id = Id<SparkDashboardSample>.FromTrusted(ids[series].Raw), Dashboard = dashboard, Series = series, At = at, LastAt = at };
                await session.StoreAsync(raw, cancellationToken);
            }
            raw.Sum = value;
            raw.Count = 1;
            session.SetExpiry(raw, rawExpiry);

            var rollup = existing.GetValueOrDefault(ids[series].Rollup);
            if (rollup is null)
            {
                rollup = new SparkDashboardSample { Id = Id<SparkDashboardSample>.FromTrusted(ids[series].Rollup), Dashboard = dashboard, Series = series, Rollup = true, At = rollupAt, LastAt = long.MinValue };
                await session.StoreAsync(rollup, cancellationToken);
            }
            if (rollup.LastAt < at)
            {
                rollup.Sum += value;
                rollup.Count++;
                rollup.LastAt = at;
            }
            session.SetExpiry(rollup, rollupExpiry);
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DashboardHistoryPoint>> ReadHistoryAsync(
        string dashboard, string series, DateTime from, DateTime to, TimeSpan bucket, CancellationToken cancellationToken)
    {
        // RavenDB's "group by N minutes": whole minutes, at least one.
        var size = Math.Max(1, (long)Math.Ceiling(bucket.TotalMinutes)) * 60_000;
        var (fromMs, toMs) = (Milliseconds(from), Milliseconds(to));
        var rawToo = bucket < options.Value.RollupInterval;

        using var session = store.OpenSession();
        var query = session.Query<SparkDashboardSample>()
            .Where(s => s.Dashboard == dashboard && s.Series == series && s.At >= fromMs && s.At <= toMs);
        if (!rawToo)
            query = query.Where(s => s.Rollup);
        var rows = await session.ToListAsync(query, cancellationToken);

        return [.. rows
            .GroupBy(r => Floor(r.At, size))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                // A bucket the raw samples still cover reads them; one they have left reads the rollups.
                var raw = g.Where(r => !r.Rollup).ToList();
                var (sum, count) = raw.Count > 0 ? (raw.Sum(r => r.Sum), raw.Count) : (g.Sum(r => r.Sum), g.Sum(r => r.Count));
                return new DashboardHistoryPoint(g.Key, count > 0 ? Math.Round(sum / count, 3) : null);
            })];
    }

    internal static string IdOf(string dashboard, string series, bool rollup, long at)
        => $"sparkdashboardsamples/{dashboard}/{series}/{(rollup ? "rollup" : "raw")}/{at}".ToLowerInvariant();

    private static long Milliseconds(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static long Floor(long value, long size) => value - (((value % size) + size) % size);
}
