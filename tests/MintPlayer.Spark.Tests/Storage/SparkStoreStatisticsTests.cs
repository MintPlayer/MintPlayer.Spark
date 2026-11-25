using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// #536 M8 (D45): <see cref="ISparkStoreStatistics"/> on the database <c>SPARK_TEST_DATABASE</c> picks, the source of the
/// Dashboards package's <c>DatabaseStatistics</c> widget. Every store counts the documents of each collection; the
/// database stores also measure the sizes on disk, which memory has none of.
/// </summary>
public class SparkStoreStatisticsTests : SparkTestDriver
{
    public sealed class StatisticsContext : SparkContext;

    public class SsEvent
    {
        public string? Id { get; set; }
        public string Title { get; set; } = "";
    }

    public class SsNote
    {
        public string? Id { get; set; }
        public string Text { get; set; } = "";
    }

    private SparkEndpointFactory<StatisticsContext> NewHost() => new(
        Database,
        [],
        configureServices: services =>
        {
            services.AddSparkStoredType<SsEvent>();
            services.AddSparkStoredType<SsNote>();
        });

    [Fact]
    public async Task Every_collection_reports_its_document_count_and_the_database_stores_their_sizes()
    {
        await using var host = NewHost();
        using (var session = host.GetService<ISparkStore>().OpenSession())
        {
            for (var i = 1; i <= 3; i++)
                await session.StoreAsync(new SsEvent { Id = $"ssevents/{i}", Title = new string('e', 500) });
            for (var i = 1; i <= 2; i++)
                await session.StoreAsync(new SsNote { Id = $"ssnotes/{i}", Text = new string('n', 500) });
            await session.SaveChangesAsync();
        }

        var store = host.GetService<ISparkStore>();
        var statistics = await host.GetService<ISparkStoreStatistics>().GetAsync();

        var events = statistics.Collections.Single(c => c.Name == SparkCollectionNames.Of(typeof(SsEvent)));
        var notes = statistics.Collections.Single(c => c.Name == SparkCollectionNames.Of(typeof(SsNote)));
        events.Documents.Should().Be(3);
        notes.Documents.Should().Be(2);
        statistics.Documents.Should().BeGreaterThanOrEqualTo(5);
        statistics.Documents.Should().Be(statistics.Collections.Sum(c => c.Documents));

        if (store.ProviderName == SparkStoreProviders.InMemory)
        {
            statistics.SizeBytes.HasValue.Should().BeFalse();
            events.SizeBytes.HasValue.Should().BeFalse();
        }
        else
        {
            statistics.SizeBytes.Should().BeGreaterThan(0);
            events.SizeBytes.Should().BeGreaterThan(0);
        }
    }
}
