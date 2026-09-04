using Microsoft.Extensions.Time.Testing;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// <see cref="MessageSequence"/> is the ordering key for every partitioned queue, so its guarantees
/// are asserted directly rather than inferred from end-to-end behaviour. No server, no fixture.
/// </summary>
public class MessageSequenceTests
{
    [Fact]
    public void Issues_strictly_increasing_values_within_one_clock_tick()
    {
        // The realistic producer shape: a loop broadcasting many messages faster than the clock
        // advances. DateTime.UtcNow alone would tie here, and ties make order arbitrary.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sequence = new MessageSequence(clock);

        var issued = Enumerable.Range(0, 1000).Select(_ => sequence.Next()).ToList();

        issued.Should().BeInAscendingOrder("every value must be greater than the last");
        issued.Distinct().Should().HaveCount(1000, "no value may repeat");
    }

    [Fact]
    public void Never_goes_backwards_when_the_clock_does()
    {
        // An NTP step backwards must not invert two messages of the same partition.
        // FakeTimeProvider refuses to move backwards ("Cannot go back in time"), which is exactly
        // the scenario under test — so this one case needs a clock that permits it.
        var start = DateTimeOffset.UtcNow;
        var clock = new RewindableClock(start);
        var sequence = new MessageSequence(clock);

        var before = sequence.Next();
        clock.Now = start.AddMinutes(-5);
        var after = sequence.Next();

        after.Should().BeGreaterThan(before, "a clock that steps backwards must not reorder messages");
    }

    [Fact]
    public void Tracks_the_clock_while_it_advances()
    {
        // Values stay roughly comparable across processes and restarts, which is what makes a
        // process-local counter acceptable as a global-ish ordering key.
        var start = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(start);
        var sequence = new MessageSequence(clock);

        sequence.Next();
        clock.Advance(TimeSpan.FromHours(1));

        sequence.Next().Should().BeGreaterThanOrEqualTo(
            start.AddHours(1).UtcTicks,
            "the sequence should follow the clock rather than drift behind it");
    }

    /// <summary>A clock that can be set to any instant, including an earlier one.</summary>
    private sealed class RewindableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Issues_unique_values_under_concurrent_callers()
    {
        // MessageBus is scoped but MessageSequence is a singleton, so concurrent broadcasts share
        // one instance. A lost update here would silently produce two messages with one sequence,
        // and their order within a partition would be decided by nothing at all.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sequence = new MessageSequence(clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
            () => Enumerable.Range(0, 500).Select(_ => sequence.Next()).ToList())));

        var all = results.SelectMany(x => x).ToList();
        all.Should().HaveCount(8000);
        all.Distinct().Should().HaveCount(8000, "concurrent callers must never receive the same value");
    }
}
