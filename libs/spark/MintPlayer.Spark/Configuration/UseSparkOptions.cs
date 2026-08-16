namespace MintPlayer.Spark.Configuration;

/// <summary>
/// Options for configuring the Spark middleware pipeline via <c>UseSpark(options => ...)</c>.
/// <para>
/// Model synchronization used to live here. It moved to the builder phase — see
/// <see cref="SparkDevelopmentExtensions.SynchronizeSparkModelsIfRequested(WebApplicationBuilder, string[])"/>
/// — because it is a build step rather than middleware: it needs no database and no request pipeline.
/// </para>
/// </summary>
public class UseSparkOptions
{
    internal IApplicationBuilder App { get; set; } = null!;
}
