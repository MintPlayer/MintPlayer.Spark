using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory.Jobs;
using MintPlayer.Spark.Commerce.Promotions.Identity;
using MintPlayer.Spark.Commerce.Promotions.Jobs;
using MintPlayer.Spark.Commerce.Promotions.Services;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce.Promotions;

/// <summary>
/// Promotions (<c>docs/commerce_PRD.md</c> §4.6, <c>docs/guide-commerce-promotions.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddCommerceCheckout();
///     spark.AddCommercePromotions();    // Spark:Commerce:Promotions; adds Checkout
/// });
/// </code>
/// </summary>
public static class SparkCommercePromotionsExtensions
{
    /// <summary>
    /// Registers codes (the pricing steps, the admission gate for unlock/presale codes, the capacity source for
    /// capacity-blocking codes), sale prices with the Omnibus prior price, the ticket waiting list, back-in-stock
    /// notifications, tracking links, the order-event recipient, the GDPR hooks, the jobs and the endpoints under
    /// <c>/spark/commerce/promotions</c>. Declares the <c>promotions</c> organization role. Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddCommercePromotions(this ISparkBuilder builder, Action<SparkCommercePromotionsOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<PromotionsMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddCommerceCheckout();

        services.AddSparkConfigurationSection<SparkCommercePromotionsOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Values that decide what gets registered are read now: code, then configuration (config wins, §4.2).
        var registrationTime = new SparkCommercePromotionsOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommercePromotionsOptions.SectionName).Bind(registrationTime);
        builder.AddOrganizationRole(registrationTime.ManagerRole, "Manages the organization's promotion codes, sale prices, waiting lists and tracking links.");

        // GA9/X21: an attribution is analytics, not strictly necessary; startup refuses without the consent primitive.
        if (registrationTime.Tracking.Enabled && registrationTime.Tracking.RequireConsent)
            services.RequiresConsent(registrationTime.Tracking.ConsentPurpose, "commerce-promotions tracking links (GC2)");
        if (registrationTime.BackInStock.Enabled && registrationTime.BackInStock.RequireMarketingConsent)
            services.RequiresConsent(SparkConsentPurposes.MarketingMail, "commerce-promotions back-in-stock mails (GB11)");

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<PromotionItems>();
        services.TryAddScoped<PromotionUsage>();
        services.TryAddScoped<PromotionBlocks>();
        services.TryAddScoped<PromotionMails>();
        services.TryAddScoped<PromotionsAccess>();
        services.TryAddScoped<WaitingLists>();
        services.TryAddScoped<BackInStock>();
        services.TryAddScoped<Tracking>();
        services.TryAddScoped<ISparkPromotionCodes, PromotionCodes>();
        services.TryAddScoped<ISparkSalePrices, SalePrices>();

        // Checkout's extension points (PRD §4.1): Checkout references none of these.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutPricingStep, SalePricePricingStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutPricingStep, PromotionCodePricingStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutAdmissionGate, PromotionAdmissionGate>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutCapacitySource, PromotionBlockCapacitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, PromotionOrderEvents>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IInventoryClaimLapsed, PromotionClaimLapsed>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkVisitorMergeHandler, PromotionVisitorMerge>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(PromotionAccountDeletion<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(PromotionPersonalData<>)));

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkCommercePromotionsExtensions).Assembly, "SparkMail/");

        builder.AddCron(cron =>
        {
            cron.AddJob<SalePriceJob>(registrationTime.SalePrices.Schedule, "Promotions.SalePrices")
                .AddJob<PromotionBlocksJob>(registrationTime.BlocksSchedule, "Promotions.WaitingListsAndBlocks");
            if (registrationTime.BackInStock.Enabled)
                cron.AddJob<BackInStockJob>(registrationTime.BackInStock.Schedule, "Promotions.BackInStock");
        });

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommercePromotionsEndpoints());
        return builder;
    }

    /// <summary>Refuses settings that cannot work; warns when a list's queue can only be closed by hand.</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<IOptions<SparkCommercePromotionsOptions>>().Value;
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(SparkCommercePromotionsExtensions));
        var problems = Problems(options);
        if (problems.Count > 0)
            throw new InvalidOperationException("Promotions cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        if (!sp.GetServices<IWaitingListQueueState>().Any())
            logger?.LogInformation("No IWaitingListQueueState is registered: a waiting list with a QueueScope activates only after staff mark its queue closed.");
    }

    internal static List<string> Problems(SparkCommercePromotionsOptions options)
    {
        const string root = "Spark:Commerce:Promotions";
        var problems = new List<string>();
        if (options.UsageReservation <= TimeSpan.Zero)
            problems.Add($"{root}:UsageReservation must be positive.");
        if (options.WaitingList.OfferValidity <= TimeSpan.Zero)
            problems.Add($"{root}:WaitingList:OfferValidity must be positive.");
        if (options.WaitingList.MaxQuantityPerEntry < 1)
            problems.Add($"{root}:WaitingList:MaxQuantityPerEntry must be at least 1.");
        if (options.BackInStock.BatchFactor <= 0)
            problems.Add($"{root}:BackInStock:BatchFactor must be positive.");
        if (options.BackInStock.MaxBatch < 1)
            problems.Add($"{root}:BackInStock:MaxBatch must be at least 1.");
        if (options.BackInStock.SubscriptionLifetime <= TimeSpan.Zero)
            problems.Add($"{root}:BackInStock:SubscriptionLifetime must be positive.");
        if (options.Tracking.AttributionWindow <= TimeSpan.Zero)
            problems.Add($"{root}:Tracking:AttributionWindow must be positive.");
        if (string.IsNullOrWhiteSpace(options.ManagerRole))
            problems.Add($"{root}:ManagerRole must name a role.");
        return problems;
    }

    private sealed class PromotionsMarker;
}
