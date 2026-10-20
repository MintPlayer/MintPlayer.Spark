using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Replication;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Barcodes;
using MintPlayer.Spark.Warehouse.Items;
using MintPlayer.Spark.Warehouse.Locations;
using MintPlayer.Spark.Warehouse.Owners;
using MintPlayer.Spark.Warehouse.Stock;
using MintPlayer.Spark.Warehouse.Work;

namespace MintPlayer.Spark.Warehouse;

/// <summary>
/// The warehouse core (<c>docs/warehouse_picking_PRD.md</c> §4.1, §4.5, WM3):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMessaging();
///     spark.AddReplication();   // Spark:Replication (module name, sources: FulfilmentOrders, FulfilmentCatalogItems, …)
///     spark.AddWarehouse();     // Spark:Warehouse; adds Organizations
/// });
/// // App_Data/security.json: "bindings": { "warehouse:supervisors": [ … ], "warehouse:staff": [ … ] }
/// </code>
/// </summary>
public static class SparkWarehouseExtensions
{
    /// <summary>
    /// Registers the stock engine, owner links and their replication row filter, items and the <c>catalog-items</c>
    /// consumer, the barcode registry and pipeline, replenishment (daily sweep), the task envelope and the recall trace;
    /// binds <c>Spark:Warehouse</c> and refuses startup on an invalid value. Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddWarehouse(this ISparkBuilder builder, Action<SparkWarehouseOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<WarehouseMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddOrganizations();

        services.AddSparkConfigurationSection<SparkWarehouseOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSingleton<IValidateOptions<SparkWarehouseOptions>, SparkWarehouseOptionsValidator>();
        services.AddOptions<SparkWarehouseOptions>().ValidateOnStart();

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);

        // Allocation rules (PRD §4.7): the built-ins; a host adds its own IAllocationRule beside them.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAllocationRule, FifoAllocationRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAllocationRule, FefoAllocationRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAllocationRule, ClosestLocationAllocationRule>());

        services.TryAddScoped<IWarehouseStock, WarehouseStock>();
        services.TryAddScoped<IStockLevelPublisher, StockLevelPublisher>();
        services.TryAddScoped<IRecallTrace, RecallTrace>();

        services.TryAddScoped<IWarehouseOwnerLinks, WarehouseOwnerLinks>();
        services.TryAddScoped<IWarehouseOwnerScope, WarehouseOwnerScope>();
        // Stock levels are high-churn: flushed as a dirty set every 2 s by default (lead decision 2026-10-20); the host
        // can add more collections in Spark:Replication:DirtySetCollections.
        builder.AddReplicaDirtySetCollection(FulfilmentCollections.StockLevels);

        // Q-W23: the shops receive only their own owners' status, shipments, stock levels and return receipts.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaRowFilter, WarehouseOwnerLinkRowFilter>());

        services.TryAddScoped<IWarehouseItems, WarehouseItems>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaChanged<FulfilmentCatalogItem>, CatalogItemConsumer>());

        services.TryAddScoped<IWarehouseLocations, WarehouseLocations>();

        services.TryAddScoped<IBarcodeResolver, BarcodeResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IBarcodeResolutionStep, Gs1BarcodeStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IBarcodeResolutionStep, OwnCodesBarcodeStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IBarcodeResolutionStep, RegistryBarcodeStep>());

        services.TryAddScoped<IWarehouseTasks, WarehouseTasks>();
        services.TryAddScoped<IWarehouseReplenishment, WarehouseReplenishment>();

        // The schedule is read at registration (the cron registry needs it now): configuration, then code.
        var registrationTime = new SparkWarehouseOptions();
        builder.Configuration?.GetSection(SparkWarehouseOptions.SectionName).Bind(registrationTime);
        configure?.Invoke(registrationTime);
        if (registrationTime.Replenishment.Mode == ReplenishmentMode.MinMax)
            builder.AddCron(cron => cron.AddJob<WarehouseReplenishmentJob>(registrationTime.Replenishment.Schedule, "Warehouse.Replenishment"));

        return builder;
    }

    private sealed class WarehouseMarker;
}
