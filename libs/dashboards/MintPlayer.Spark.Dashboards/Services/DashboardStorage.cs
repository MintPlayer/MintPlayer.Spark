using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Operations.TimeSeries;
using Raven.Client.Documents.Queries.TimeSeries;
using Raven.Client.ServerWide.Operations;
using Sparrow;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>The backing document of a dashboard's history (D14): it holds the time series and nothing else.</summary>
internal sealed class SparkDashboard
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// The <c>SparkDashboards</c> collection (D14, D29): its time-series policies and revisions, the samples written
/// into it and the history read back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration merges, never replaces.</b> <c>ConfigureTimeSeriesOperation</c> replaces every collection's
/// time-series configuration (spike S11, "merge"), so the policies go through
/// <c>ConfigureTimeSeriesPolicyOperation</c> and <c>ConfigureRawTimeSeriesPolicyOperation</c>, which touch one
/// collection. <c>ConfigureRevisionsOperation</c> also replaces everything, so revisions are a read-modify-write of
/// this one collection, as History's <c>RevisionsConfigurator</c> does. Nothing is sent when it already matches.
/// </para>
/// <para>
/// <b>Single-valued series.</b> With six values per entry the rollup and retention policies silently never ran
/// (S11), so a sample is one <see cref="double"/> per series; there is no other way to append.
/// </para>
/// </remarks>
internal sealed class DashboardStorage(IDocumentStore store, IOptions<SparkDashboardsOptions> options, ILogger<DashboardStorage> logger)
{
    public const string Collection = "SparkDashboards";

    /// <summary>The rollup policy's name: a rolled-up series is <c>{name}@rollup</c>.</summary>
    public const string RollupPolicy = "rollup";

    public static string DocumentId(string dashboard) => $"{Collection}/{dashboard}";

    // ---- configuration -------------------------------------------------------------------------------------

    /// <summary>Applies the collection's revisions and time-series policies; returns what it changed.</summary>
    public async Task<IReadOnlyList<string>> ConfigureAsync(CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        if (await DisableRevisionsAsync(cancellationToken))
            changed.Add("revisions");
        changed.AddRange(await ConfigureTimeSeriesAsync(cancellationToken));
        if (changed.Count > 0)
            logger.LogInformation("Configured the {Collection} collection: {Changes}.", Collection, string.Join(", ", changed));
        return changed;
    }

    private async Task<bool> DisableRevisionsAsync(CancellationToken cancellationToken)
    {
        var configuration = await store.Maintenance.SendAsync(new GetRevisionsConfigurationOperation(), cancellationToken) ?? new RevisionsConfiguration();
        configuration.Collections ??= new Dictionary<string, RevisionsCollectionConfiguration>();

        // Collection names compare case-insensitively in RavenDB; reuse the stored key's spelling.
        var key = configuration.Collections.Keys.FirstOrDefault(k => string.Equals(k, Collection, StringComparison.OrdinalIgnoreCase)) ?? Collection;
        if (configuration.Collections.TryGetValue(key, out var existing) && existing is { Disabled: true })
            return false;

        configuration.Collections[key] = new RevisionsCollectionConfiguration { Disabled = true };
        await store.Maintenance.SendAsync(new ConfigureRevisionsOperation(configuration), cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<string>> ConfigureTimeSeriesAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var raw = new RawTimeSeriesPolicy(Time(o.RawRetention));
        var rollup = new TimeSeriesPolicy(RollupPolicy, Time(o.RollupInterval), Time(o.RollupRetention));

        TimeSeriesCollectionConfiguration? current = null;
        var known = false;
        try
        {
            var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database), cancellationToken);
            known = true;
            current = record?.TimeSeries?.Collections?
                .FirstOrDefault(c => string.Equals(c.Key, Collection, StringComparison.OrdinalIgnoreCase)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reading the record needs more than a database admin may have; the two operations below are
            // idempotent, so without it they are simply sent.
            logger.LogDebug(ex, "Could not read the database record; sending the {Collection} time-series policies unchecked.", Collection);
        }

        var changed = new List<string>();
        if (!known || current?.RawPolicy is not { } currentRaw || !currentRaw.RetentionTime.Equals(raw.RetentionTime))
        {
            await store.Maintenance.SendAsync(new ConfigureRawTimeSeriesPolicyOperation(Collection, raw), cancellationToken);
            changed.Add("raw retention");
        }
        if (!known || current?.Policies?.FirstOrDefault(p => string.Equals(p.Name, RollupPolicy, StringComparison.OrdinalIgnoreCase)) is not { } currentRollup
            || !currentRollup.AggregationTime.Equals(rollup.AggregationTime) || !currentRollup.RetentionTime.Equals(rollup.RetentionTime))
        {
            await store.Maintenance.SendAsync(new ConfigureTimeSeriesPolicyOperation(Collection, rollup), cancellationToken);
            changed.Add("rollup");
        }
        return changed;
    }

    private static TimeValue Time(TimeSpan span) => TimeValue.FromSeconds(checked((int)Math.Round(span.TotalSeconds)));

    // ---- samples -------------------------------------------------------------------------------------------

    /// <summary>
    /// Appends one point per series at <paramref name="timestamp"/> (a slot start): a second write of the same slot
    /// overwrites the point instead of adding one.
    /// </summary>
    public async Task AppendAsync(string dashboard, DateTime timestamp, IReadOnlyDictionary<string, double> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
            return;

        var id = DocumentId(dashboard);
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<SparkDashboard>(id, cancellationToken) is null)
            await session.StoreAsync(new SparkDashboard { Id = id, Name = dashboard }, id, cancellationToken);
        foreach (var (series, value) in samples)
            session.TimeSeriesFor(id, RqlIdentifier.Collection(series)).Append(timestamp, value);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The averages of <paramref name="series"/> between <paramref name="from"/> and <paramref name="to"/> in buckets of
    /// <paramref name="bucket"/> (D29: one grouped query, every value a parameter). The raw name falls back to the rollup
    /// where the raw points have been retired.
    /// </summary>
    public async Task<IReadOnlyList<DashboardHistoryPoint>> ReadHistoryAsync(
        string dashboard, string series, DateTime from, DateTime to, TimeSpan bucket, CancellationToken cancellationToken)
    {
        // The series name is an identifier: it cannot be a parameter, so it passes the shared allow-list first and is
        // quoted (a name such as "group" is then not a keyword).
        var name = RqlIdentifier.Collection(series);
        var rql = $"from {Collection} where id() = $id select timeseries(from '{name}' between $from and $to group by $g select avg())";

        using var session = store.OpenAsyncSession();
        var results = await session.Advanced.AsyncRawQuery<TimeSeriesAggregationResult>(rql)
            .AddParameter("id", DocumentId(dashboard))
            .AddParameter("from", from)
            .AddParameter("to", to)
            .AddParameter("g", $"{Math.Max(1, (int)Math.Ceiling(bucket.TotalMinutes))} minutes")
            .ToListAsync(cancellationToken);

        return results.SelectMany(r => r.Results)
            .Select(r => new DashboardHistoryPoint(
                new DateTimeOffset(DateTime.SpecifyKind(r.From, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                r.Average is { Length: > 0 } average ? Math.Round(average[0], 3) : null))
            .ToList();
    }
}
