using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>The suite against RavenDB's compare-exchange values.</summary>
public sealed class RavenAtomicValuesConformanceTests : SparkAtomicValuesConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<ISparkAtomicValues?> CreateAsync()
    {
        await driver.InitializeAsync();
        return new RavenAtomicValues(driver.DocumentStore);
    }

    public override async Task DisposeAsync() => await driver.DisposeAsync();

    /// <summary>
    /// A compare-exchange value written before #536 as a <see cref="DateTime"/> (the migration runner's old lock) reads as
    /// its text, so the lease can still recognise it.
    /// </summary>
    [Fact]
    public async Task A_value_written_as_a_DateTime_reads_as_its_round_trip_text()
    {
        var at = new DateTime(2026, 11, 25, 10, 30, 0, DateTimeKind.Utc);
        await driver.DocumentStore.Operations.SendAsync(new PutCompareExchangeValueOperation<DateTime>("conf/legacy", at, 0));

        var read = await Values.GetAsync("conf/legacy");

        DateTime.Parse(read!.Value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().Should().Be(at);
    }

    private sealed class Driver : RavenSparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store's values.</summary>
public sealed class InMemoryAtomicValuesConformanceTests : SparkAtomicValuesConformanceTests
{
    protected override Task<ISparkAtomicValues?> CreateAsync() => Task.FromResult<ISparkAtomicValues?>(new InMemoryAtomicValues());
}
