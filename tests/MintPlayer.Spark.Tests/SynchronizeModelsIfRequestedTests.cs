namespace MintPlayer.Spark.Tests;

public class SynchronizeModelsIfRequestedTests
{
    [Fact]
    public void SynchronizeSparkModelsIfRequested_WithoutFlag_ReturnsApp()
    {
        var args = new[] { "--some-other-flag", "--verbose" };

        args.Contains("--spark-synchronize-model").Should().BeFalse();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_WithFlag_IsDetected()
    {
        var args = new[] { "--spark-synchronize-model" };

        args.Contains("--spark-synchronize-model").Should().BeTrue();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_WithMixedArgs_IsDetected()
    {
        var args = new[] { "--verbose", "--spark-synchronize-model", "--other" };

        args.Contains("--spark-synchronize-model").Should().BeTrue();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_EmptyArgs_NotDetected()
    {
        var args = Array.Empty<string>();

        args.Contains("--spark-synchronize-model").Should().BeFalse();
    }
}
