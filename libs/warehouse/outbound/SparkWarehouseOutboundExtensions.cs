using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Outbound.Services;
using MintPlayer.Spark.Warehouse.Outbound.Strategies;
using MintPlayer.Spark.Warehouse.Stock;

namespace MintPlayer.Spark.Warehouse.Outbound;

/// <summary>
/// Warehouse Outbound (<c>docs/warehouse_picking_PRD.md</c> §4.3, §4.7, WM5):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMessaging();
///     spark.AddReplication();          // Spark:Replication:Sources:FulfilmentOrders = [ "Shop" ]
///     spark.AddWarehouseOutbound();    // adds the warehouse core; Spark:Warehouse:Routing, Release, Picking, ShortPick, Cancellation
/// });
/// // App_Data/security.json: "bindings": { "warehouse-outbound:pickers": [ … ], "warehouse-outbound:supervisors": [ … ] }
/// </code>
/// </summary>
public static class SparkWarehouseOutboundExtensions
{
    /// <summary>
    /// Registers intake (the <c>fulfilment-orders</c> consumer and site routing), release and allocation, the built-in pick
    /// strategies and router, picking, short picks, the cancellation handshake, consolidation, backorders, waves and the
    /// recall observer; binds Outbound's part of <c>Spark:Warehouse</c> and refuses startup on an invalid value.
    /// </summary>
    public static ISparkBuilder AddWarehouseOutbound(this ISparkBuilder builder, Action<SparkWarehouseOutboundOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<OutboundMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddWarehouse();

        services.AddSparkConfigurationSection<SparkWarehouseOutboundOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSingleton<IValidateOptions<SparkWarehouseOutboundOptions>, SparkWarehouseOutboundOptionsValidator>();
        services.AddOptions<SparkWarehouseOutboundOptions>().ValidateOnStart();
        services.TryAddSingleton<Claims.ISparkClaims, Claims.SparkClaims>();

        AddServices(services);

        // Waves run every minute and release each site's due cut-off once (Q-W1); backorders are retried every 5 minutes.
        var registrationTime = new SparkWarehouseOutboundOptions();
        builder.Configuration?.GetSection(SparkWarehouseOutboundOptions.SectionName).Bind(registrationTime);
        configure?.Invoke(registrationTime);
        builder.AddCron(cron =>
        {
            if (registrationTime.Release.Mode == ReleaseMode.Waves)
                cron.AddJob<OutboundWaveJob>(OutboundWaveJob.CronSchedule, "Warehouse.Outbound.Waves");
            cron.AddJob<OutboundBackorderJob>(OutboundBackorderJob.CronSchedule, "Warehouse.Outbound.Backorders");
        });
        return builder;
    }

    /// <summary>The services without the Spark host (tests compose them over the core's).</summary>
    internal static void AddServices(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPickStrategy, DiscretePickStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPickStrategy, BatchPickStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPickStrategy, ClusterPickStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPickStrategy, ZonePickStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPickRouter, WalkSequencePickRouter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISiteRouter, ConfigurationSiteRouter>());

        services.TryAddScoped<OutboundService>();
        services.TryAddScoped<IOutboundIntake>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddScoped<IOutboundRelease>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddScoped<IOutboundWaves>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddScoped<IOutboundPicking>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddScoped<IOutboundCancellation>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddScoped<IOutboundConsolidation>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddScoped<IOutboundBackorders>(sp => sp.GetRequiredService<OutboundService>());
        services.AddScoped<ILotStatusObserver>(sp => sp.GetRequiredService<OutboundService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaChanged<FulfilmentOrder>, FulfilmentOrderConsumer>());
    }

    private sealed class OutboundMarker;
}

/// <summary>Releases each site's due wave cut-off (PRD §4.7 Release, Q-W1). Runs every minute; each cut-off once, cluster-wide.</summary>
internal sealed partial class OutboundWaveJob : ISparkCronJob
{
    public static string CronSchedule => "0 * * * * *";

    [Inject] private readonly IOutboundWaves waves;

    public Task RunAsync(CancellationToken cancellationToken) => waves.RunDueAsync(cancellationToken);
}

/// <summary>Re-allocates backordered lines once stock arrived (PRD §4.7). Every 5 minutes.</summary>
internal sealed partial class OutboundBackorderJob : ISparkCronJob
{
    public static string CronSchedule => "*/5 * * * *";

    [Inject] private readonly IOutboundBackorders backorders;
    [Inject] private readonly ILogger<OutboundBackorderJob> logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var released = await backorders.RetryAsync(cancellationToken: cancellationToken);
        if (released > 0)
            logger.LogInformation("Backorders: {Count} order(s) got pick work.", released);
    }
}
