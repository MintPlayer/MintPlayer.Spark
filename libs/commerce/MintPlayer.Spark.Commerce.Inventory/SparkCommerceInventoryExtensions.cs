using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Commerce.Inventory.Jobs;
using MintPlayer.Spark.Commerce.Inventory.Live;
using MintPlayer.Spark.Commerce.Inventory.Storage;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary>
/// The inventory engine (<c>docs/commerce_PRD.md</c> §4.3, <c>docs/guide-commerce-inventory.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddCommerceInventory();      // Spark:Commerce:Inventory; checkout calls it itself
/// });
/// await inventory.CreateQuotaAsync("quotas/arena-floor", capacity: 10_000, shardCount: 32);
/// await inventory.MapItemAsync("tickets/weekend", [new("quotas/day-1"), new("quotas/day-2")]);
/// var held = await inventory.TryClaimAsync(new ClaimRequest("claims/b-17", "baskets/17", now.AddMinutes(10), [],
///     Items: [new("tickets/weekend", 2)], Limits: InventoryLimitKeys.For("sales/42", max: 4, n: 2, buyer)));
/// </code>
/// </summary>
public static class SparkCommerceInventoryExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkInventory"/> (the engine, a singleton), the sweeper (cron, every few seconds), the live
    /// availability topics <c>commerce-inventory:{quotaId}</c>, and the startup checks of S18 (revisions off on the
    /// engine's collections; no persistent-object type over them). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddCommerceInventory(this ISparkBuilder builder, Action<SparkCommerceInventoryOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<InventoryMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        services.AddSparkConfigurationSection<SparkCommerceInventoryOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InventoryAvailabilityPublisher>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SparkCommerceInventoryOptions>>().Value;
            var publisher = sp.GetRequiredService<InventoryAvailabilityPublisher>();
            InventoryEngine? engine = null;
            engine = new InventoryEngine(
                sp.GetRequiredService<IDocumentStore>(),
                database: null,
                options.ToEngineOptions(),
                sp.GetRequiredService<TimeProvider>(),
                options.PublishOnCommit ? (quotas, ct) => publisher.PublishAsync(engine!, quotas, ct) : null);
            return engine;
        });
        services.TryAddSingleton<ISparkInventory>(sp => sp.GetRequiredService<InventoryEngine>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkLiveTopicPolicy, InventoryLiveTopicPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, InventoryStoreSetup>());

        // The schedule is read at registration (the Cron registry needs it now): code, then configuration.
        var registrationTime = new SparkCommerceInventoryOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommerceInventoryOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron.AddJob<InventorySweepJob>(registrationTime.SweepSchedule, "Commerce.Inventory.Sweep"));

        builder.Registry.AddMiddleware(app => InventoryCollections.Validate(app.ApplicationServices.GetRequiredService<IModelLoader>()));
        return builder;
    }

    private sealed class InventoryMarker;
}
