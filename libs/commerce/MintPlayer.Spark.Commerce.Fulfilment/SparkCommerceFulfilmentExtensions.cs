using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Fulfilment.Services;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Commerce.Fulfilment;

/// <summary>
/// The shop side of the fulfilment contracts (<c>docs/commerce_PRD.md</c> §4.9, <c>docs/warehouse_picking_PRD.md</c> §4.3–§4.4):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMessaging();
///     spark.AddReplication();
///     spark.AddCommerceShop();          // Spark:Commerce:Shop:Fulfilment = "Warehouse"
///     spark.AddCommerceFulfilment();    // Spark:Commerce:Fulfilment:ShopName = "shop-a"
/// });
/// </code>
/// </summary>
public static partial class SparkCommerceFulfilmentExtensions
{
    /// <summary>
    /// Registers the shop side: published <c>fulfilment-orders</c>, <c>catalog-items</c> and <c>expected-returns</c> (model types
    /// of this library, granted to the <c>commerce-fulfilment:warehouses</c> slot), the consumers of <c>fulfilment-status</c>,
    /// <c>shipments</c>, <c>stock-levels</c> and <c>return-receipts</c>, Checkout's stock-feed seam and order events, the
    /// reconcile and the catalog sweep, and the recall notice. Adds the shop. Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddCommerceFulfilment(this ISparkBuilder builder, Action<SparkCommerceFulfilmentOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<FulfilmentMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddCommerceShop();

        services.AddSparkConfigurationSection<SparkCommerceFulfilmentOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Schedules are read at registration (the cron registry needs them now): code, then configuration.
        var registrationTime = new SparkCommerceFulfilmentOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommerceFulfilmentOptions.SectionName).Bind(registrationTime);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<FulfilmentCatalog>();
        services.TryAddScoped<FulfilmentStock>();
        services.TryAddScoped<FulfilmentSync>();
        services.TryAddScoped<FulfilmentRecalls>();
        services.TryAddScoped<ISparkCommerceFulfilment, SparkCommerceFulfilment>();

        // Checkout's extension points (commerce PRD §4.1): Checkout never references this package.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutStockFeed, FulfilmentStockFeed>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, FulfilmentOrderEvents>());

        // The warehouse's facts (replication's change notifications, HTTP transport).
        services.TryAddScoped<FulfilmentReplicaHandlers>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaChanged<FulfilmentOrderStatus>, FulfilmentReplicaHandlers>(sp => sp.GetRequiredService<FulfilmentReplicaHandlers>()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaChanged<FulfilmentShipment>, FulfilmentReplicaHandlers>(sp => sp.GetRequiredService<FulfilmentReplicaHandlers>()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaChanged<FulfilmentReturnReceipt>, FulfilmentReplicaHandlers>(sp => sp.GetRequiredService<FulfilmentReplicaHandlers>()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReplicaChanged<FulfilmentStockLevel>, FulfilmentReplicaHandlers>(sp => sp.GetRequiredService<FulfilmentReplicaHandlers>()));

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkCommerceFulfilmentExtensions).Assembly, "SparkMail/");

        builder.AddCron(cron => cron
            .AddJob<FulfilmentReconcileJob>(registrationTime.ReconcileSchedule, "Fulfilment.Reconcile")
            .AddJob<FulfilmentCatalogJob>(registrationTime.CatalogSchedule, "Fulfilment.CatalogAndStock"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        return builder;
    }

    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var problems = Problems(
            sp.GetRequiredService<IOptions<SparkCommerceFulfilmentOptions>>().Value,
            sp.GetRequiredService<IOptions<SparkCommerceShopOptions>>().Value,
            sp.GetService<IOptions<SparkReplicationOptions>>()?.Value,
            hasPublisher: sp.GetService<IReplicaPublisher>() is not null);
        if (problems.Count > 0)
            throw new InvalidOperationException("Commerce fulfilment cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    /// <summary>The warehouse-owned collections the shop consumes (warehouse §4.3).</summary>
    internal static readonly string[] ConsumedCollections =
        [FulfilmentCollections.OrderStatuses, FulfilmentCollections.Shipments, FulfilmentCollections.StockLevels, FulfilmentCollections.ReturnReceipts];

    /// <summary>What makes the configuration unusable. Pure.</summary>
    internal static List<string> Problems(SparkCommerceFulfilmentOptions options, SparkCommerceShopOptions shop, SparkReplicationOptions? replication, bool hasPublisher)
    {
        const string root = "Spark:Commerce:Fulfilment";
        var problems = new List<string>();
        if (string.IsNullOrEmpty(options.ShopName) || !ShopNamePattern().IsMatch(options.ShopName))
            problems.Add($"{root}:ShopName is required: letters, digits, '-', '_' and '.' (it prefixes every published id, unique across the shops a warehouse serves).");
        if (shop.Fulfilment != ShopFulfilmentMode.Warehouse)
            problems.Add("Spark:Commerce:Shop:Fulfilment must be \"Warehouse\" when the warehouse ships (otherwise the shop books shipments itself).");
        if (!hasPublisher || replication is null)
            problems.Add("Commerce fulfilment needs replication: call spark.AddMessaging() and spark.AddReplication().");
        else
        {
            var unbound = ConsumedCollections.Where(c => !replication.Sources.TryGetValue(c, out var sources) || sources.Length == 0).ToList();
            if (unbound.Count > 0)
                problems.Add("Spark:Replication:Sources must name the warehouse module for " + string.Join(", ", unbound)
                    + ", e.g. \"Sources\": { " + string.Join(", ", unbound.Select(c => $"\"{c}\": [ \"Warehouse\" ]")) + " }.");
        }
        foreach (var (name, channel) in options.Channels)
            if (channel.Buffer < 0)
                problems.Add($"{root}:Channels:{name}:Buffer cannot be negative.");
        if (options.ReconcileBatchSize < 1)
            problems.Add($"{root}:ReconcileBatchSize must be at least 1.");
        if (options.DeliveryWatch <= TimeSpan.Zero)
            problems.Add($"{root}:DeliveryWatch must be positive.");
        if (string.IsNullOrWhiteSpace(options.RecallMailTemplate))
            problems.Add($"{root}:RecallMailTemplate is required.");
        return problems;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ShopNamePattern();

    private sealed class FulfilmentMarker;
}
