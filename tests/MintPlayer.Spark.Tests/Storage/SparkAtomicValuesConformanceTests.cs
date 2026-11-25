using MintPlayer.Spark.Abstractions.Storage;


namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// <see cref="ISparkAtomicValues"/>, the neutral compare-exchange (#536 S15), as every provider keeps it: one suite, run
/// once per provider.
/// </summary>
public abstract class SparkAtomicValuesConformanceTests : IAsyncLifetime
{
    private ISparkAtomicValues? values;

    /// <summary>The provider's values; a provider with no server configured skips the test here.</summary>
    protected ISparkAtomicValues Values => values ?? throw new SkipException("No server is configured for this provider.");

    /// <summary>The values under test, or <see langword="null"/> when the provider's server is not configured.</summary>
    protected abstract Task<ISparkAtomicValues?> CreateAsync();

    public virtual async Task InitializeAsync() => values = await CreateAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_missing_key_reads_as_null()
        => (await Values.GetAsync("conf/missing")).Should().BeNull();

    [SkippableFact]
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

    [SkippableFact]
    public async Task A_write_naming_the_read_version_replaces_the_value_and_changes_the_version()
    {
        var first = (await Values.TryPutAsync("conf/b", "one", 0))!;

        var second = await Values.TryPutAsync("conf/b", "two", first.Version);

        second.Should().NotBeNull();
        second!.Version.Should().NotBe(first.Version);
        (await Values.GetAsync("conf/b"))!.Value.Should().Be("two");
    }

    [SkippableFact]
    public async Task A_write_naming_a_stale_version_changes_nothing()
    {
        var first = (await Values.TryPutAsync("conf/c", "one", 0))!;
        await Values.TryPutAsync("conf/c", "two", first.Version);

        (await Values.TryPutAsync("conf/c", "three", first.Version)).Should().BeNull();
        (await Values.GetAsync("conf/c"))!.Value.Should().Be("two");
    }

    [SkippableFact]
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

    [SkippableFact]
    public async Task Concurrent_creates_have_one_winner()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Values.TryPutAsync("conf/race", $"w{i}", 0)));

        results.Count(r => r is not null).Should().Be(1);
    }
}
