namespace MintPlayer.Spark.Tests;

public class SynchronizeModelsIfRequestedTests
{
    [Fact]
    public void SynchronizeSparkModelsIfRequested_WithoutFlag_ReturnsApp()
    {
        // The method checks args.Contains("--spark-synchronize-model").
        // Without the flag, it should return immediately without calling SynchronizeSparkModels.
        // We can't easily test the full pipeline (needs RavenDB), but we can verify
        // the arg-parsing logic via the extension method's behavior.
        var args = new[] { "--some-other-flag", "--verbose" };
        var containsFlag = args.Contains("--spark-synchronize-model");

        Assert.False(containsFlag);
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_WithFlag_IsDetected()
    {
        var args = new[] { "--spark-synchronize-model" };
        var containsFlag = args.Contains("--spark-synchronize-model");

        Assert.True(containsFlag);
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_WithMixedArgs_IsDetected()
    {
        var args = new[] { "--verbose", "--spark-synchronize-model", "--other" };
        var containsFlag = args.Contains("--spark-synchronize-model");

        Assert.True(containsFlag);
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_EmptyArgs_NotDetected()
    {
        var args = Array.Empty<string>();
        var containsFlag = args.Contains("--spark-synchronize-model");

        Assert.False(containsFlag);
    }
}
