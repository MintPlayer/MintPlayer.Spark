using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication;

public static class SparkBuilderReplicationExtensions
{
    /// <summary>
    /// The configuration section replication binds, matching the operator guide.
    /// </summary>
    private const string ConfigurationSection = "Spark:Replication";

    /// <summary>
    /// Adds Spark cross-module ETL replication services.
    /// <para>
    /// Binds <c>Spark:Replication</c> first, then applies <paramref name="configure"/>, so code
    /// wins over configuration and settings that cannot be expressed in JSON (assemblies to
    /// scan) stay in code. Binding here rather than in each app is the fix for F2: hosts used to
    /// hand-map four properties by name, which meant every key the operator guide documents but
    /// nobody had hand-mapped — the whole <c>ClientCertificate</c> node, mTLS included — bound to
    /// nothing, silently and with no error.
    /// </para>
    /// </summary>
    public static ISparkBuilder AddReplication(
        this ISparkBuilder builder,
        Action<SparkReplicationOptions> configure)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);

        builder.Services.AddSparkReplication(options =>
        {
            section?.Bind(options);
            configure(options);
        });

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
