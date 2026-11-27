using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Dashboards;
using MintPlayer.Spark.Dashboards.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Dashboards;

/// <summary>
/// #536 D45: dashboard history on the database <c>SPARK_TEST_DATABASE</c> picks, wired as <c>AddDashboards</c> wires it.
/// RavenDB keeps its time series; SQL Server, PostgreSQL and memory keep <see cref="SparkDashboardSample"/> rows and
/// their rollups. The lease is an <see cref="ISparkAtomicValues"/> value on every store.
/// </summary>
public class NeutralDashboardHistoryTests : SparkTestDriver
{
    public sealed class HistoryContext : SparkContext;

    private SparkEndpointFactory<HistoryContext> NewHost() => new(
        Database,
        [],
        configureServices: services => services.AddDashboardHistory());

    private static DashboardCatalog Catalog()
    {
        var catalog = new DashboardCatalog();
        var definition = catalog.GetOrAdd("HtDashboard");
        definition.AddProvider(typeof(HtProvider));
        definition.History.Add("Gauge");
        return catalog;
    }

    /// <summary>One sampler host: its own lease identity, the host's store.</summary>
    private static (DashboardSampler Sampler, IDashboardHistory History) Sampler(SparkEndpointFactory<HistoryContext> host, DashboardCatalog catalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton(host.GetService<ISparkStore>());
        services.AddSingleton(host.GetService<ISparkAtomicValues>());
        if (host.GetService<ISparkStore>().ProviderName == SparkStoreProviders.RavenDb)
            services.AddSingleton(host.GetService<Raven.Client.Documents.IDocumentStore>());
        services.AddSingleton(catalog);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new SparkDashboardsOptions()));
        services.AddDashboardHistory();
        services.AddSingleton<DashboardReader>();
        services.AddSingleton<IHostApplicationLifetime, NoLifetime>();
        services.AddSingleton<DashboardSampler>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<DashboardSampler>(), provider.GetRequiredService<IDashboardHistory>());
    }

    /// <summary>The hour before this one: recent enough that no sample has expired.</summary>
    private static DateTime LastHour()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
    }

    [Fact]
    public async Task Two_concurrent_samplers_claim_each_slot_once()
    {
        await using var host = NewHost();
        var catalog = Catalog();
        var (a, history) = Sampler(host, catalog);
        var (b, _) = Sampler(host, catalog);
        var first = a.SlotOf(DateTime.UtcNow);
        const int slots = 10;

        var claims = new List<(bool A, bool B)>();
        for (var slot = first; slot < first + slots; slot++)
        {
            var s = slot;
            var both = await Task.WhenAll(Task.Run(() => a.TrySampleAsync(s, CancellationToken.None)), Task.Run(() => b.TrySampleAsync(s, CancellationToken.None)));
            claims.Add((both[0], both[1]));
        }

        claims.Should().OnlyContain(c => c.A ^ c.B, "each slot is claimed by exactly one host");
        var points = await history.ReadHistoryAsync("HtDashboard", "Gauge", a.StartOf(first), a.StartOf(first + slots), TimeSpan.FromMinutes(1), CancellationToken.None);
        points.Select(p => p.X).Should().Equal(Enumerable.Range(0, slots).Select(i => new DateTimeOffset(a.StartOf(first + i)).ToUnixTimeMilliseconds()), "one point per slot");
        points.Should().OnlyContain(p => p.Y == 42.5);
    }

    [Fact]
    public async Task A_slot_already_sampled_is_not_sampled_again_and_an_older_one_is_refused()
    {
        await using var host = NewHost();
        var catalog = Catalog();
        var (a, _) = Sampler(host, catalog);
        var (b, _) = Sampler(host, catalog);
        var slot = a.SlotOf(DateTime.UtcNow);

        (await a.TrySampleAsync(slot, CancellationToken.None)).Should().BeTrue();
        (await a.TrySampleAsync(slot, CancellationToken.None)).Should().BeFalse("the holder itself does not sample a slot twice");
        (await b.TrySampleAsync(slot + 1, CancellationToken.None)).Should().BeFalse("a's lease is live");
        (await a.TrySampleAsync(slot - 1, CancellationToken.None)).Should().BeFalse("an older slot than the last one sampled");
    }

    [Fact]
    public async Task The_history_is_read_in_buckets_of_averages_and_a_slot_written_twice_counts_once()
    {
        await using var host = NewHost();
        var (_, history) = Sampler(host, Catalog());
        var start = LastHour();
        for (var minute = 0; minute < 10; minute++)
            await history.AppendAsync("HtDashboard", start.AddMinutes(minute), new Dictionary<string, double> { ["Gauge"] = minute }, CancellationToken.None);
        // A slot written again overwrites its point: the averages do not move.
        await history.AppendAsync("HtDashboard", start.AddMinutes(3), new Dictionary<string, double> { ["Gauge"] = 3 }, CancellationToken.None);

        // At the rollup interval (5 minutes) and above, the rollups answer; below it, the raw samples.
        var coarse = await history.ReadHistoryAsync("HtDashboard", "Gauge", start, start.AddMinutes(10), TimeSpan.FromMinutes(5), CancellationToken.None);
        var fine = await history.ReadHistoryAsync("HtDashboard", "Gauge", start, start.AddMinutes(10), TimeSpan.FromMinutes(1), CancellationToken.None);

        coarse.Select(p => p.Y).Should().Equal(2.0, 7.0);
        coarse[0].X.Should().Be(new DateTimeOffset(start).ToUnixTimeMilliseconds());
        fine.Select(p => p.Y).Should().Equal(Enumerable.Range(0, 10).Select(i => (double?)i));
    }

    [Fact]
    public async Task Another_dashboard_or_series_is_not_read()
    {
        await using var host = NewHost();
        var (_, history) = Sampler(host, Catalog());
        var start = LastHour();
        await history.AppendAsync("HtDashboard", start, new Dictionary<string, double> { ["Gauge"] = 1, ["Other"] = 100 }, CancellationToken.None);
        await history.AppendAsync("HtOther", start, new Dictionary<string, double> { ["Gauge"] = 200 }, CancellationToken.None);

        var points = await history.ReadHistoryAsync("HtDashboard", "Gauge", start, start.AddMinutes(10), TimeSpan.FromMinutes(1), CancellationToken.None);

        points.Select(p => p.Y).Should().Equal(1.0);
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
