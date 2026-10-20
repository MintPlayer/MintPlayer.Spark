using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication;

public static class SparkBuilderReplicationExtensions
{
    /// <summary>
    /// The configuration section replication binds, matching the operator guide.
    /// </summary>
    private const string ConfigurationSection = "Spark:Replication";

    /// <summary>
    /// Adds Spark cross-module replication (warehouse §4.2): the replica classes this module declares
    /// (<c>[Replicated]</c>, source-generated), registration with their owners, the approval gate for what other modules
    /// read from this one, delivery by RavenDB ETL or over messaging and mTLS, the reconcile, and write-back.
    /// <para>
    /// Binds <c>Spark:Replication</c> first, then applies <paramref name="configure"/>, so code wins over
    /// configuration (the fix for F2: hosts used to hand-map four properties by name, so the whole
    /// <c>ClientCertificate</c> node, mTLS included, bound to nothing). Needs <c>AddMessaging()</c>: registrations,
    /// pushes and queued write-backs ride its queue, and replication creates no RavenDB subscription of its own.
    /// </para>
    /// </summary>
    public static ISparkBuilder AddReplication(
        this ISparkBuilder builder,
        Action<SparkReplicationOptions>? configure = null)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);

        builder.Services.AddSparkReplication(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });

        // Schedules are read at registration (the cron registry needs them now): configuration, then code.
        var registrationTime = new SparkReplicationOptions { ModuleName = string.Empty, ModuleUrl = string.Empty };
        section?.Bind(registrationTime);
        configure?.Invoke(registrationTime);
        builder.AddCron(cron =>
        {
            cron.AddJob<ReplicaReconcileJob>(registrationTime.Reconcile.Schedule, "Replication.Reconcile");
            if (registrationTime.DirtySetCollections.Count > 0)
                cron.AddJob<ReplicaDirtySetFlushJob>(registrationTime.Push.DirtySetFlushSchedule, "Replication.DirtySetFlush");
        });

        // Tombstones and applied pending marks expire.
        builder.Registry.RequireDocumentExpiration("Replication");

        builder.Registry.AddMiddleware(app =>
        {
            if (app is WebApplication webApp)
                SparkReplicationExtensions.UseSparkReplication(webApp);
        });

        builder.Registry.AddEndpoints(endpoints => SparkReplicationExtensions.MapSparkReplication(endpoints));

        return builder;
    }
}
