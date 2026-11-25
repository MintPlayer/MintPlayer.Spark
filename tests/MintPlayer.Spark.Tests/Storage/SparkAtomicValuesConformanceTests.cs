using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// <see cref="ISparkAtomicValues"/>, the neutral compare-exchange (#536 S15), as every provider keeps it: one suite, run
/// once per provider.
/// </summary>
public abstract class SparkAtomicValuesConformanceTests : IAsyncLifetime
{
    protected ISparkAtomicValues Values { get; private set; } = null!;

    protected abstract Task<ISparkAtomicValues> CreateAsync();

    public virtual async Task InitializeAsync() => Values = await CreateAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_missing_key_reads_as_null()
        => (await Values.GetAsync("conf/missing")).Should().BeNull();

    [Fact]
    public async Task Version_zero_creates_a_value_only_when_there_is_none()
    {
        var created = await Values.TryPutAsync("conf/a", "one", 0);

        created.Should().NotBeNull();
        created!.Value.Should().Be("one");
        created.Version.Should().BeGreaterThan(0);
        (await Values.TryPutAsync("conf/a", "two", 0)).Should().BeNull("a value already exists");

        var read = await Values.GetAsync("conf/a");
        read!.Value.Should().Be("one");
        read.Version.Should().Be(created.Version);
    }

    [Fact]
    public async Task A_write_naming_the_read_version_replaces_the_value_and_changes_the_version()
    {
        var first = (await Values.TryPutAsync("conf/b", "one", 0))!;

        var second = await Values.TryPutAsync("conf/b", "two", first.Version);

        second.Should().NotBeNull();
        second!.Version.Should().NotBe(first.Version);
        (await Values.GetAsync("conf/b"))!.Value.Should().Be("two");
    }

    [Fact]
    public async Task A_write_naming_a_stale_version_changes_nothing()
    {
        var first = (await Values.TryPutAsync("conf/c", "one", 0))!;
        await Values.TryPutAsync("conf/c", "two", first.Version);

        (await Values.TryPutAsync("conf/c", "three", first.Version)).Should().BeNull();
        (await Values.GetAsync("conf/c"))!.Value.Should().Be("two");
    }

    [Fact]
    public async Task A_delete_needs_the_current_version()
    {
        var first = (await Values.TryPutAsync("conf/d", "one", 0))!;
        var second = (await Values.TryPutAsync("conf/d", "two", first.Version))!;

        (await Values.TryDeleteAsync("conf/d", first.Version)).Should().BeFalse();
        (await Values.GetAsync("conf/d")).Should().NotBeNull();

        (await Values.TryDeleteAsync("conf/d", second.Version)).Should().BeTrue();
        (await Values.GetAsync("conf/d")).Should().BeNull();
        (await Values.TryPutAsync("conf/d", "again", 0)).Should().NotBeNull("a deleted key is free again");
    }

    [Fact]
    public async Task Concurrent_creates_have_one_winner()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Values.TryPutAsync("conf/race", $"w{i}", 0)));

        results.Count(r => r is not null).Should().Be(1);
    }
}

/// <summary>The suite against RavenDB's compare-exchange values.</summary>
public sealed class RavenAtomicValuesConformanceTests : SparkAtomicValuesConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<ISparkAtomicValues> CreateAsync()
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

    private sealed class Driver : SparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store's values.</summary>
public sealed class InMemoryAtomicValuesConformanceTests : SparkAtomicValuesConformanceTests
{
    protected override Task<ISparkAtomicValues> CreateAsync() => Task.FromResult<ISparkAtomicValues>(new InMemoryAtomicValues());
}
