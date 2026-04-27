using MintPlayer.Spark.SubscriptionWorker;

namespace MintPlayer.Spark.Tests.SubscriptionWorker;

public class SparkSubscriptionOptionsTests
{
    [Fact]
    public void Defaults_are_two_minute_timeout_and_wait_for_non_stale_indexes()
    {
        var options = new SparkSubscriptionOptions();

        options.WaitForNonStaleIndexes.Should().BeTrue();
        options.NonStaleIndexTimeout.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Properties_are_settable()
    {
        var options = new SparkSubscriptionOptions
        {
            WaitForNonStaleIndexes = false,
            NonStaleIndexTimeout = TimeSpan.FromSeconds(30),
        };

        options.WaitForNonStaleIndexes.Should().BeFalse();
        options.NonStaleIndexTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
