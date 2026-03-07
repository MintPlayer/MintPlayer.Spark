using Microsoft.AspNetCore.Builder;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication;

public static class SparkBuilderReplicationExtensions
{
    /// <summary>
    /// Adds Spark cross-module ETL replication services.
    /// </summary>
    public static ISparkBuilder AddReplication(
        this ISparkBuilder builder,
        Action<SparkReplicationOptions> configure)
    {
        builder.Services.AddSparkReplication(configure);

        // Register middleware callback for replication startup
        builder.Registry.AddMiddleware(app =>
        {
            if (app is WebApplication webApp)
            {
                SparkReplicationExtensions.UseSparkReplication(webApp);
            }
        });

        // Register endpoint callback
        builder.Registry.AddEndpoints(endpoints =>
            SparkReplicationExtensions.MapSparkReplication(endpoints));

        return builder;
    }
}
