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

    [Fact]
    public async Task TrackRetryAsync_schedules_refresh_and_returns_true_when_under_MaxAttempts()
    {
        var (session, counters, metadata) = MakeSession(currentCounterValue: 0);
        var numerator = new RetryNumerator { MaxAttempts = 5 };
        var entity = new FakeEntity();

        var willRetry = await numerator.TrackRetryAsync(session, entity, new Exception("boom"));

        willRetry.Should().BeTrue();
        counters.Received(1).Increment("SparkRetryAttempts", 1);
        counters.DidNotReceive().Delete("SparkRetryAttempts");
        metadata.Should().ContainKey("@refresh");
        // @refresh is a near-future timestamp (within the linear-backoff window)
        var refreshAt = DateTime.Parse((string)metadata["@refresh"]!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        refreshAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task TrackRetryAsync_parks_the_document_and_returns_false_when_MaxAttempts_reached()
    {
        var (session, counters, metadata) = MakeSession(currentCounterValue: 5);
        var numerator = new RetryNumerator { MaxAttempts = 5, ExhaustedDelay = TimeSpan.FromDays(1) };
        var entity = new FakeEntity();

        var willRetry = await numerator.TrackRetryAsync(session, entity, new Exception("boom"));

        willRetry.Should().BeFalse();
        counters.Received(1).Delete("SparkRetryAttempts");
        metadata.Should().ContainKey("@refresh");
        var refreshAt = DateTime.Parse((string)metadata["@refresh"]!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        refreshAt.Should().BeOnOrAfter(DateTime.UtcNow.AddHours(23)); // ~1 day park
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

        var ts1 = await RefreshTimestampAfterAttemptAsync(numerator, attempt: 1);
        var ts3 = await RefreshTimestampAfterAttemptAsync(numerator, attempt: 3);

        var delay1 = (ts1 - DateTime.UtcNow).TotalSeconds;
        var delay3 = (ts3 - DateTime.UtcNow).TotalSeconds;
        delay3.Should().BeGreaterThan(delay1, "later attempts wait longer");
        delay3.Should().BeApproximately(delay1 * 3, 2.0, "linear scaling of BaseDelay * attempt");
    }

    private static async Task<DateTime> RefreshTimestampAfterAttemptAsync(RetryNumerator numerator, int attempt)
    {
        var (session, _, metadata) = MakeSession(currentCounterValue: attempt - 1);
        await numerator.TrackRetryAsync(session, new FakeEntity(), new Exception("boom"));
        return DateTime.Parse((string)metadata["@refresh"]!, null, System.Globalization.DateTimeStyles.RoundtripKind);
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
