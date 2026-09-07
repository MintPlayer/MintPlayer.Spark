using MintPlayer.Spark.SubscriptionWorker;
using NSubstitute;
using Raven.Client.Documents.Session;
using Raven.Client.Json;

namespace MintPlayer.Spark.Tests.SubscriptionWorker;

public class RetryNumeratorTests
{
    [Fact]
    public void GetDelay_uses_BaseDelay_multiplied_by_attempt_number_with_minimum_one()
    {
        var n = new RetryNumerator { BaseDelay = TimeSpan.FromSeconds(10) };

        n.GetDelay(attempt: 0).Should().Be(TimeSpan.FromSeconds(10), "attempt 0 is clamped to 1");
        n.GetDelay(attempt: 1).Should().Be(TimeSpan.FromSeconds(10));
        n.GetDelay(attempt: 3).Should().Be(TimeSpan.FromSeconds(30));
        n.GetDelay(attempt: 10).Should().Be(TimeSpan.FromSeconds(100));
    }

    /// <summary>
    /// These three facts used to assert that <c>@metadata.@refresh</c> was written, and that its
    /// value tracked the computed delay. Those writes are gone, so the assertions are rewritten to
    /// the outcome the caller actually acts on — <see cref="RetryOutcome.NextAttemptAtUtc"/> —
    /// rather than adapted.
    /// <para>
    /// The writes were <b>inert</b>: nothing in this repository ever sent
    /// <c>ConfigureRefreshOperation</c>, so the server never swept those documents and the metadata
    /// caused no redelivery. Worse, <c>RetryOutcome</c>'s own documentation asserted that
    /// <c>@refresh</c> could not gate change-vector-driven redelivery, so these tests were pinning
    /// a mechanism the code next to them said did not work. Redelivery is driven by projecting
    /// <c>NextAttemptAtUtc</c> onto a queryable field and letting a sweeper flip a boolean gate.
    /// </para>
    /// <para>
    /// For the record the disclaimer was false and the mechanism does work — measured 2026-09-07 on
    /// RavenDB 7.2, a document was withheld while <c>@refresh</c> was set and delivered 4.65 s after
    /// the sweep removed it. Adopting it would mean sending the configuration and asserting it at
    /// startup; see spike S4 in <c>docs/messaging_single_subscription_plan.md</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TrackRetryAsync_schedules_the_next_attempt_and_returns_WillRetry_true_when_under_MaxAttempts()
    {
        var (session, counters, metadata) = MakeSession(currentCounterValue: 0);
        var numerator = new RetryNumerator { MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(30) };
        var entity = new FakeEntity();

        var outcome = await numerator.TrackRetryAsync(session, entity, new Exception("boom"));

        outcome.WillRetry.Should().BeTrue();
        outcome.AttemptCount.Should().Be(1);
        outcome.NextAttemptAtUtc.Should().BeAfter(DateTime.UtcNow);
        outcome.NextAttemptAtUtc.Should().BeCloseTo(
            DateTime.UtcNow + TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        counters.Received(1).Increment("SparkRetryAttempts", 1);
        counters.DidNotReceive().Delete("SparkRetryAttempts");

        metadata.Should().NotContainKey("@refresh",
            "the write was inert — refresh is never configured — and reads as a working mechanism");
    }

    [Fact]
    public async Task TrackRetryAsync_parks_the_document_and_returns_WillRetry_false_when_MaxAttempts_reached()
    {
        var (session, counters, metadata) = MakeSession(currentCounterValue: 5);
        var numerator = new RetryNumerator { MaxAttempts = 5, ExhaustedDelay = TimeSpan.FromDays(1) };
        var entity = new FakeEntity();

        var outcome = await numerator.TrackRetryAsync(session, entity, new Exception("boom"));

        outcome.WillRetry.Should().BeFalse();
        outcome.AttemptCount.Should().Be(6);
        outcome.NextAttemptAtUtc.Should().BeOnOrAfter(DateTime.UtcNow.AddHours(23));
        counters.Received(1).Delete("SparkRetryAttempts");
        metadata.Should().NotContainKey("@refresh");
    }

    [Fact]
    public async Task ClearRetryAsync_deletes_the_counter_from_the_session()
    {
        var (session, counters, _) = MakeSession(currentCounterValue: 0);
        var numerator = new RetryNumerator();
        var entity = new FakeEntity();

        await numerator.ClearRetryAsync(session, entity);

        counters.Received(1).Delete("SparkRetryAttempts");
    }

    [Fact]
    public async Task Counter_name_override_is_respected_everywhere()
    {
        var (session, counters, _) = MakeSession(currentCounterValue: 0);
        var numerator = new RetryNumerator { CounterName = "CustomCounter" };
        var entity = new FakeEntity();

        await numerator.TrackRetryAsync(session, entity, new Exception("boom"));
        await numerator.ClearRetryAsync(session, entity);

        counters.Received(1).Increment("CustomCounter", 1);
        counters.Received(1).Delete("CustomCounter");
        counters.DidNotReceive().Increment("SparkRetryAttempts", Arg.Any<long>());
    }

    [Fact]
    public async Task Linear_backoff_grows_with_attempt_number()
    {
        var numerator = new RetryNumerator { BaseDelay = TimeSpan.FromSeconds(10), MaxAttempts = 10 };

        var ts1 = await NextAttemptAfterAttemptAsync(numerator, attempt: 1);
        var ts3 = await NextAttemptAfterAttemptAsync(numerator, attempt: 3);

        var delay1 = (ts1 - DateTime.UtcNow).TotalSeconds;
        var delay3 = (ts3 - DateTime.UtcNow).TotalSeconds;
        delay3.Should().BeGreaterThan(delay1, "later attempts wait longer");
        delay3.Should().BeCloseTo(delay1 * 3, 2.0, "linear scaling of BaseDelay * attempt");
    }

    /// <summary>
    /// Reads the schedule off the returned outcome. It used to parse it back out of the
    /// <c>@refresh</c> metadata, which meant the assertion depended on a side effect that did
    /// nothing rather than on the value the caller is handed.
    /// </summary>
    private static async Task<DateTime> NextAttemptAfterAttemptAsync(RetryNumerator numerator, int attempt)
    {
        var (session, _, _) = MakeSession(currentCounterValue: attempt - 1);
        var outcome = await numerator.TrackRetryAsync(session, new FakeEntity(), new Exception("boom"));
        return outcome.NextAttemptAtUtc;
    }

    private static (IAsyncDocumentSession Session, IAsyncSessionDocumentCounters Counters, IMetadataDictionary Metadata) MakeSession(long currentCounterValue)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var counters = Substitute.For<IAsyncSessionDocumentCounters>();
        var metadata = new MetadataAsDictionary(new Dictionary<string, object>());

        session.CountersFor(Arg.Any<object>()).Returns(counters);

        // Counter value rises by +1 after Increment
        var value = currentCounterValue;
        counters.When(c => c.Increment(Arg.Any<string>(), Arg.Any<long>()))
            .Do(call => value += (long)call[1]);
        counters.GetAsync(Arg.Any<string>()).Returns(_ => Task.FromResult<long?>(value));

        var advanced = Substitute.For<IAsyncAdvancedSessionOperations>();
        advanced.GetMetadataFor(Arg.Any<object>()).Returns(metadata);
        advanced.GetDocumentId(Arg.Any<object>()).Returns("fake/1");
        session.Advanced.Returns(advanced);

        return (session, counters, metadata);
    }

    private sealed class FakeEntity
    {
        public string? Id { get; set; }
    }
}
