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
        });
        if (registrationTime.DirtySetCollections.Count > 0)
            EnsureDirtySetFlush(builder, registrationTime.Push.DirtySetFlushSchedule);

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

    /// <summary>
    /// A library contributes a high-churn source collection to the dirty set (warehouse §4.2.3; lead decision 2026-10-20:
    /// the warehouse registers <c>FulfilmentStockLevels</c> by default): its changes are marked and flushed every
    /// <c>Spark:Replication:Push:DirtySetFlushSchedule</c> instead of pushed one by one. Works before or after
    /// <see cref="AddReplication"/>; the host's <c>Spark:Replication:DirtySetCollections</c> can still add more. The flush
    /// job is registered once, whichever call comes first.
    /// </summary>
    public static ISparkBuilder AddReplicaDirtySetCollection(this ISparkBuilder builder, string sourceCollection)
    {
        if (!Abstractions.Rendering.ReplicaScriptRenderer.IsIdentifier(sourceCollection))
            throw new ArgumentException($"'{sourceCollection}' is not a collection name.", nameof(sourceCollection));
        builder.Services.PostConfigure<SparkReplicationOptions>(options => options.DirtySetCollections.Add(sourceCollection));
        EnsureDirtySetFlush(builder);
        return builder;
    }

    /// <summary>Registers the dirty-set flush job once, on the schedule configuration names (or the default).</summary>
    private static void EnsureDirtySetFlush(ISparkBuilder builder, string? scheduleFromCode = null)
    {
        if (!builder.Services.TryAddSparkMarker<DirtySetFlushMarker>())
            return;
        var schedule = scheduleFromCode
            ?? (builder.Configuration?.GetSection(ConfigurationSection + ":Push:DirtySetFlushSchedule").Value is { Length: > 0 } configured ? configured : null)
            ?? new ReplicationPushOptions().DirtySetFlushSchedule;
        builder.AddCron(cron => cron.AddJob<ReplicaDirtySetFlushJob>(schedule, "Replication.DirtySetFlush"));
    }

    private sealed class DirtySetFlushMarker;
}
