using MintPlayer.Spark.Sequences;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Sequences;

/// <summary>
/// Warehouse WM2: the compare-exchange sequence behind SSCC allocation. Unique values per key, across concurrent
/// callers and separate instances, starting at the requested first value.
/// </summary>
public class SparkSequencesTests : SparkTestDriver
{
    [Fact]
    public async Task A_sequence_starts_at_its_first_value_and_counts_up()
    {
        var sequences = new SparkSequences(Store);

        (await sequences.PeekAsync("tests/seq/a")).HasValue.Should().BeFalse();
        (await sequences.NextAsync("tests/seq/a", firstValue: 1)).Should().Be(1);
        (await sequences.NextAsync("tests/seq/a", firstValue: 1)).Should().Be(2);
        var last = await sequences.PeekAsync("tests/seq/a");
        last.HasValue.Should().BeTrue();
        last!.Value.Should().Be(2);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var sequences = new SparkSequences(Store);

        await sequences.NextAsync("tests/seq/b");
        await sequences.NextAsync("tests/seq/b");

        (await sequences.NextAsync("tests/seq/c")).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_callers_on_two_instances_never_get_the_same_value()
    {
        // Two instances stand in for two pack stations (two processes): only compare-exchange decides.
        var first = new SparkSequences(Store);
        var second = new SparkSequences(Store);

        var values = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => (i % 2 == 0 ? first : second).NextAsync("tests/seq/sscc")));

        values.Distinct().Count().Should().Be(40);
        values.Order().Should().Equal(Enumerable.Range(0, 40).Select(i => (long)i).ToArray());
    }
}
