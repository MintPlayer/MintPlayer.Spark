using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Dashboards;
using MintPlayer.Spark.Dashboards.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Operations.TimeSeries;
using Raven.Client.Documents.Session.TimeSeries;
using Raven.Client.ServerWide.Operations;
using Sparrow;

namespace MintPlayer.Spark.Tests.Dashboards;

/// <summary>
/// Dashboard history on RavenDB time series (D14, D29, spike S11) against the embedded server: one point per slot
/// under two concurrent samplers, single-valued series, and a collection configuration that merges instead of
/// replacing another collection's policies or revisions.
/// </summary>
public class DashboardHistoryTests : SparkTestDriver
{
    /// <summary>One sampler host: its own lease identity, the shared database.</summary>
    private DashboardSampler Sampler(DashboardCatalog catalog, IDocumentStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store ?? Store);
        services.AddSingleton(catalog);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new SparkDashboardsOptions()));
        services.AddSingleton<DashboardStorage>();
        services.AddSingleton<DashboardReader>();
        services.AddSingleton<DashboardSamplerLease>();
        services.AddSingleton<IHostApplicationLifetime, NoLifetime>();
        services.AddSingleton<DashboardSampler>();
        return services.BuildServiceProvider().GetRequiredService<DashboardSampler>();
    }

    private static DashboardCatalog Catalog(params string[] history)
    {
        var catalog = new DashboardCatalog();
        var definition = catalog.GetOrAdd("HtDashboard");
        definition.AddProvider(typeof(HtProvider));
        foreach (var name in history)
            definition.History.Add(name);
        return catalog;
    }

    private DashboardStorage Storage(SparkDashboardsOptions? options = null)
        => new(Store, Options.Create(options ?? new SparkDashboardsOptions()), NullLogger<DashboardStorage>.Instance);

    private async Task<TimeSeriesEntry[]> EntriesAsync(string series)
    {
        using var session = Store.OpenAsyncSession();
        return await session.TimeSeriesFor(DashboardStorage.DocumentId("HtDashboard"), series).GetAsync() ?? [];
    }

    [Fact]
    public async Task Two_concurrent_samplers_write_exactly_one_point_per_slot()
    {
        var catalog = Catalog("Gauge");
        var a = Sampler(catalog);
        using var otherClient = new DocumentStore { Urls = Store.Urls, Database = Store.Database, Certificate = Store.Certificate }.Initialize();
        var b = Sampler(catalog, otherClient);
        var first = a.SlotOf(DateTime.UtcNow);
        const int slots = 25;

        var claims = new List<(long Slot, bool A, bool B)>();
        for (var slot = first; slot < first + slots; slot++)
        {
            var s = slot;
            var both = await Task.WhenAll(Task.Run(() => a.TrySampleAsync(s, CancellationToken.None)), Task.Run(() => b.TrySampleAsync(s, CancellationToken.None)));
            claims.Add((slot, both[0], both[1]));
        }

        claims.Should().OnlyContain(c => c.A ^ c.B, "each slot is claimed by exactly one host");
        var entries = await EntriesAsync("Gauge");
        entries.Should().HaveCount(slots);
        entries.Select(e => e.Timestamp).Should().Equal(Enumerable.Range(0, slots).Select(i => a.StartOf(first + i)), "slot-aligned, one per slot");
        entries.Should().OnlyContain(e => e.Values.Length == 1, "a series is single-valued (D29)");
    }

    [Fact]
    public async Task A_slot_already_sampled_is_not_sampled_again_and_an_older_one_is_refused()
    {
        var catalog = Catalog("Gauge");
        var a = Sampler(catalog);
        var b = Sampler(catalog);
        var slot = a.SlotOf(DateTime.UtcNow);

        (await a.TrySampleAsync(slot, CancellationToken.None)).Should().BeTrue();
        (await a.TrySampleAsync(slot, CancellationToken.None)).Should().BeFalse("the holder itself does not sample a slot twice");
        (await b.TrySampleAsync(slot + 1, CancellationToken.None)).Should().BeFalse("a's lease is live");
        (await a.TrySampleAsync(slot - 1, CancellationToken.None)).Should().BeFalse("an older slot than the last one sampled");
        (await EntriesAsync("Gauge")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Only_numbers_and_meters_are_sampled_one_value_per_series()
    {
        var catalog = Catalog("Gauge", "Meter", "Table", "Text");
        var sampler = Sampler(catalog);

        (await sampler.TrySampleAsync(sampler.SlotOf(DateTime.UtcNow), CancellationToken.None)).Should().BeTrue();

        (await EntriesAsync("Gauge")).Single().Values.Should().Equal(42.5);
        (await EntriesAsync("Meter")).Single().Values.Should().Equal(30.0); // a meter contributes its value, never its max
        (await EntriesAsync("Table")).Should().BeEmpty("a table has no single number: a series is single-valued");
        (await EntriesAsync("Text")).Should().BeEmpty();
        DashboardValues.SampleOf(new[] { 1.0, 2.0 }).HasValue.Should().BeFalse("an array is not a sample");
    }

    [Fact]
    public void A_history_name_that_is_not_a_plain_identifier_is_refused()
    {
        var catalog = new DashboardCatalog();
        var builder = new SparkDashboardsBuilder(catalog);

        var act = () => builder.Add("HtDashboard", d => d.History("Disk') select x"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Configuring_the_collection_merges_with_other_collections_and_disables_its_revisions()
    {
        // Another collection's time-series policies and revisions, as an application or History configured them.
        await Store.Maintenance.SendAsync(new ConfigureRawTimeSeriesPolicyOperation("AppOwned", new RawTimeSeriesPolicy(TimeValue.FromDays(3))));
        await Store.Maintenance.SendAsync(new ConfigureTimeSeriesPolicyOperation("AppOwned", new TimeSeriesPolicy("hourly", TimeValue.FromHours(1), TimeValue.FromDays(30))));
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new() { ["AppOwned"] = new RevisionsCollectionConfiguration { Disabled = false, MinimumRevisionsToKeep = 2 } },
        }));

        var changed = await Storage().ConfigureAsync(CancellationToken.None);
        var again = await Storage().ConfigureAsync(CancellationToken.None);

        changed.Should().Equal("revisions", "raw retention", "rollup");
        again.Should().BeEmpty("nothing is sent when the configuration already matches");

        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));
        var appOwned = record.TimeSeries.Collections["AppOwned"];
        appOwned.RawPolicy.RetentionTime.Should().Be(TimeValue.FromDays(3), "another collection's raw policy is kept");
        appOwned.Policies.Should().ContainSingle(p => p.Name == "hourly");
        var dashboards = record.TimeSeries.Collections[DashboardStorage.Collection];
        dashboards.RawPolicy.RetentionTime.Should().Be(TimeValue.FromDays(7));
        dashboards.Policies.Should().ContainSingle(p => p.Name == DashboardStorage.RollupPolicy
            && p.AggregationTime.Equals(TimeValue.FromMinutes(5)) && p.RetentionTime.Equals(TimeValue.FromDays(90)));

        record.Revisions.Collections["AppOwned"].MinimumRevisionsToKeep.Should().Be(2, "another collection's revisions are kept");
        record.Revisions.Collections[DashboardStorage.Collection].Disabled.Should().BeTrue();
    }

    [Fact]
    public async Task The_history_is_read_in_buckets_of_averages()
    {
        var storage = Storage();
        var start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var minute = 0; minute < 10; minute++)
        {
            await storage.AppendAsync("HtDashboard", start.AddMinutes(minute),
                new Dictionary<string, double> { ["Gauge"] = minute }, CancellationToken.None);
        }

        var points = await storage.ReadHistoryAsync("HtDashboard", "Gauge", start, start.AddMinutes(10), TimeSpan.FromMinutes(5), CancellationToken.None);

        points.Select(p => p.Y).Should().Equal(2.0, 7.0);
        points[0].X.Should().Be(new DateTimeOffset(start).ToUnixTimeMilliseconds());
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

/// <summary>One of each kind of value: a number, a meter, a table and a text.</summary>
public sealed class HtProvider : IDashboardProvider
{
    public Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        values.Set("Gauge", 42.5);
        values.Set("Meter", DashboardValues.Meter(30, 100));
        values.Set("Table", new List<Dictionary<string, object?>> { new() { ["name"] = "a" } });
        values.Set("Text", "not a number");
        return Task.CompletedTask;
    }
}
