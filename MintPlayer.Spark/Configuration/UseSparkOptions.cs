namespace MintPlayer.Spark.Configuration;

/// <summary>
/// Options for configuring the Spark middleware pipeline via <c>UseSpark(options => ...)</c>.
/// </summary>
public class UseSparkOptions
{
    internal IApplicationBuilder App { get; set; } = null!;

    /// <summary>
    /// Synchronizes entity model JSON files if the <c>--spark-synchronize-model</c>
    /// command-line argument is present, then exits the application.
    /// </summary>
    public UseSparkOptions SynchronizeModelsIfRequested<TContext>(string[] args)
        where TContext : SparkContext, new()
    {
        SparkExtensions.SynchronizeSparkModelsIfRequested<TContext>(App, args);
        return this;
    }
}
