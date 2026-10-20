using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Delivery;
using MintPlayer.Spark.Commerce.Shop.Discovery;
using MintPlayer.Spark.Commerce.Shop.Feeds;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Commerce.Shop.Reviews;
using MintPlayer.Spark.Commerce.Shop.Services;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Commerce.Shop;

/// <summary>
/// The shop (<c>docs/commerce_PRD.md</c> §4.10, <c>docs/guide-commerce-shop.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMolliePayments();      // or another provider
///     spark.AddCommerceShipping().UseSendcloud();  // or another carrier adapter
///     spark.AddCommerceShop();        // Spark:Commerce:Shop; adds Checkout and Shipping
/// });
/// // Spark:Commerce:Channels:shop { "Allocation": "HoldFromSelection", … }
/// </code>
/// </summary>
public static class SparkCommerceShopExtensions
{
    /// <summary>
    /// Registers the shop types (<c>ShopProduct</c>, <c>ShopVariant</c>, <c>ShopCategory</c>, <c>ShopOrder</c>,
    /// <c>ShopProductReview</c>) and their rights, variants as a checkout capacity source with their refund terms, the
    /// checkout delivery step (a pricing step plus the standard delivery cost of a withdrawal, GA1), the order and
    /// shipment events (Paid → Shipped → Delivered, returns, proof of dispatch), the shipment row filter per seller, the
    /// guarantee notice on the confirmation (GA8), reviews (GC5) with their GDPR hooks, product feeds (GC6), the public
    /// endpoints, page meta tags (X6) with schema.org Product data and the sitemap source (X22), the DSA notice target
    /// <c>product</c> and the catalogue's ranking parameters (GA5). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddCommerceShop(this ISparkBuilder builder, Action<SparkCommerceShopOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<ShopMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddCommerceCheckout();
        builder.AddCommerceShipping();

        services.AddSparkConfigurationSection<SparkCommerceShopOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ShopAccess>();
        services.TryAddScoped<ShopPublisher>();
        services.TryAddScoped<SparkShopCatalog>();
        services.TryAddScoped<ISparkShopCatalog>(sp => sp.GetRequiredService<SparkShopCatalog>());
        services.TryAddScoped<ISparkShopDelivery, ShopDelivery>();
        services.TryAddScoped<ShopReturnMails>();
        services.TryAddScoped<ShopOrders>();
        services.TryAddScoped<ISparkShopOrders>(sp => sp.GetRequiredService<ShopOrders>());
        services.TryAddScoped<ISparkShopStock>(sp => sp.GetRequiredService<ShopOrders>());
        services.TryAddScoped<ShopReviews>();
        services.TryAddScoped<ISparkShopReviews>(sp => sp.GetRequiredService<ShopReviews>());
        services.TryAddScoped<ISparkShopFeeds, ShopFeeds>();

        // Checkout's extension points (PRD §4.1): Checkout never references Shop.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutCapacitySource, ShopCapacitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRefundTermsSource, ShopRefundTermsSource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStandardDeliveryCost, ShopStandardDeliveryCost>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutPricingStep, ShopDeliveryPricingStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, ShopOrderEvents>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutMailAttachments, ShopGuaranteeNotice>());
        // Shipping's: delivery and return tracking, and a seller's members see their own shipments.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IShipmentEventRecipient, ShopShipmentEvents>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IShipmentRowFilter, ShopShipmentRowFilter>());
        // Commerce's: DSA notices (GA5), ranking transparency (GA5).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDsaNoticeTargetResolver, ShopDsaTargetResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRankingParameterSource, ShopRankingParameters>());
        // GB14: the seller's exports of its shop orders and of its products' reviews.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISellerExportSource, ShopOrdersExport>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISellerExportSource, ShopReviewsExport>());
        // GDPR (GC5): the buyer's reviews in the personal-data export, deleted with the account.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(ShopReviewPersonalData<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(ShopReviewAccountDeletion<>)));

        builder.AddInterceptor<ShopProductInterceptor>();
        builder.AddInterceptor<ShopVariantInterceptor>();
        builder.AddInterceptor<ShopCategoryInterceptor>();

        builder.AddPageMetaProvider<ShopPageMetaProvider>();
        builder.AddSitemapSource<ShopSitemapSource>();

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommerceShopEndpoints());
        return builder;
    }

    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var problems = Problems(sp.GetRequiredService<IOptions<SparkCommerceShopOptions>>().Value, sp.GetRequiredService<IOptions<SparkCommerceOptions>>().Value);
        if (problems.Count > 0)
            throw new InvalidOperationException("The shop cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    /// <summary>What makes the configuration unusable. Pure.</summary>
    internal static List<string> Problems(SparkCommerceShopOptions options, SparkCommerceOptions commerce)
    {
        const string root = "Spark:Commerce:Shop";
        var problems = new List<string>();
        if (!commerce.Channels.ContainsKey(options.DefaultChannel))
            problems.Add($"{root}:DefaultChannel names '{options.DefaultChannel}', which Spark:Commerce:Channels does not define.");
        foreach (var (name, path) in new[] { ("ProductPagePath", options.ProductPagePath), ("CategoryPagePath", options.CategoryPagePath) })
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || !path.Contains("{slug}", StringComparison.Ordinal))
                problems.Add($"{root}:{name} must be a path with {{slug}}.");
        if (options.ShardsPerQuota < 1)
            problems.Add($"{root}:ShardsPerQuota must be at least 1.");
        if (options.LowStockThreshold < 0)
            problems.Add($"{root}:LowStockThreshold cannot be negative.");
        // Directive (EU) 2019/771 Art. 10(1): at least two years (a member state may set longer, Art. 10(3); unconfirmed, D37).
        if (options.LegalGuaranteeMonths < 24)
            problems.Add($"{root}:LegalGuaranteeMonths must be at least 24 (Directive (EU) 2019/771 Art. 10(1)).");
        if (options.Delivery.ChoiceLifetime <= TimeSpan.Zero)
            problems.Add($"{root}:Delivery:ChoiceLifetime must be positive.");
        if (options.Delivery.DefaultWeightGrams < 1)
            problems.Add($"{root}:Delivery:DefaultWeightGrams must be at least 1.");
        if (options.Reviews.MaxLength < 1 || options.Reviews.Window <= TimeSpan.Zero)
            problems.Add($"{root}:Reviews: MaxLength and Window must be positive.");
        if (options.Feeds.MaxItems < 1 || options.Feeds.CacheDuration < TimeSpan.Zero)
            problems.Add($"{root}:Feeds: MaxItems must be at least 1 and CacheDuration not negative.");
        if (options.Facets.MaxPageSize < 1)
            problems.Add($"{root}:Facets:MaxPageSize must be at least 1.");
        if (string.IsNullOrWhiteSpace(options.Returns.ReturnLabelMailTemplate))
            problems.Add($"{root}:Returns:ReturnLabelMailTemplate is required.");
        return problems;
    }

    private sealed class ShopMarker;
}
