using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Payments.Jobs;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Commerce.Payments.Webhooks;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// Payments (<c>docs/commerce_PRD.md</c> §4.5, <c>docs/guide-commerce-payments.md</c>). A host adds a provider package,
/// which adds this one:
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMessaging();          // webhooks are handled from the spark-webhooks queue
///     spark.AddMolliePayments();     // or AddStripePayments(), AddTestPayments(); Spark:Commerce:Payments:Mollie
/// });
/// </code>
/// </summary>
public static class SparkCommercePaymentsExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkPayments"/>, the payment types and their rights, Connect onboarding, the KYC gate hook,
    /// disputes, the settlement import and the fee ledger. Safe to call more than once (each adapter calls it). Adds
    /// Commerce (which declares the <c>finance</c> organization role).
    /// </summary>
    public static ISparkBuilder AddCommercePayments(this ISparkBuilder builder, Action<SparkCommercePaymentsOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<PaymentsMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        // Commerce declares the `finance` and `seller-admin` organization roles these rows are read under.
        builder.AddCommerce();

        services.AddSparkConfigurationSection<SparkCommercePaymentsOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ConnectTokenProtector>();
        services.TryAddScoped<PaymentProviderRegistry>();
        services.TryAddScoped<PaymentUrls>();
        services.TryAddScoped<IPaymentMerchantResolver, PaymentMerchantResolver>();
        services.TryAddScoped<PlatformFeeLedger>();
        services.TryAddScoped<PaymentNotices>();
        services.TryAddScoped<PaymentEventProcessor>();
        services.TryAddScoped<SparkPayments>();
        services.TryAddScoped<ISparkPayments>(sp => sp.GetRequiredService<SparkPayments>());
        services.TryAddScoped<SellerOnboarding>();
        services.TryAddScoped<SettlementReconciler>();
        services.TryAddScoped<FinanceAccess>();
        // Commerce's KYC gate hook (D21).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISellerKycSource, PaymentsKycSource>());

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkCommercePaymentsExtensions).Assembly, "SparkMail/");

        // Schedules are read at registration (the Cron registry needs them now): code, then configuration.
        var registrationTime = new SparkCommercePaymentsOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommercePaymentsOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron
            .AddJob<SettlementImportJob>(registrationTime.Settlements.ImportSchedule, "Payments.SettlementImport")
            .AddJob<KycRefreshJob>(registrationTime.Connect.KycRefreshSchedule, "Payments.KycRefresh")
            .AddJob<ChargebackFinalityJob>(ChargebackFinalityJob.CronSchedule, "Payments.ChargebackFinality"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommercePaymentsEndpoints());
        return builder;
    }

    /// <summary>
    /// Registers a provider adapter (for adapter packages): the provider, its method catalog, and its webhook source
    /// <c>payments-{name}</c> handled from the queue. <paramref name="configureWebhook"/> sets the verification (Stripe's
    /// signature, Mollie's fetch-by-id) and, for signed providers only, the delivery id.
    /// </summary>
    public static ISparkBuilder AddPaymentProvider<TProvider>(this ISparkBuilder builder, string name, Action<WebhookSourceBuilder> configureWebhook)
        where TProvider : class, IPaymentProvider
    {
        ArgumentNullException.ThrowIfNull(configureWebhook);
        builder.AddCommercePayments();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IPaymentProvider, TProvider>());
        return builder.AddPaymentWebhookSource(PaymentWebhookSources.For(name), configureWebhook);
    }

    /// <summary>A further webhook source of a provider (Stripe's Connect endpoint, which has its own signing secret).</summary>
    public static ISparkBuilder AddPaymentWebhookSource(this ISparkBuilder builder, string sourceName, Action<WebhookSourceBuilder> configureWebhook)
        => builder.AddWebhookSource(sourceName, source =>
        {
            configureWebhook(source);
            source.UseHandler<PaymentWebhookHandler>();
        });

    /// <summary>
    /// No provider, an ambiguous default, two providers of one name, or connected mode with a default provider that
    /// cannot onboard sellers refuse startup.
    /// </summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IPaymentProvider>().ToList();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SparkCommercePaymentsOptions>>().Value;
        var commerce = scope.ServiceProvider.GetRequiredService<IOptions<SparkCommerceOptions>>().Value;
        var problems = new List<string>();

        if (providers.Count == 0)
            problems.Add("No payment provider is registered: add AddMolliePayments(), AddStripePayments() or AddTestPayments().");
        foreach (var duplicate in providers.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Two payment providers are named '{duplicate.Key}'.");
        if (providers.Count > 1 && string.IsNullOrEmpty(options.Provider))
            problems.Add("Several payment providers are registered: set Spark:Commerce:Payments:Provider.");
        if (options.Provider is { Length: > 0 } configured && !providers.Any(p => string.Equals(p.Name, configured, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"Spark:Commerce:Payments:Provider names '{configured}', which is not registered.");

        if (problems.Count == 0 && commerce.Merchant == MerchantMode.ConnectedSellers)
        {
            var registry = scope.ServiceProvider.GetRequiredService<PaymentProviderRegistry>();
            var main = registry.Get(null);
            if (!main.Capabilities.ConnectOnboarding || registry.Onboarding(main.Name) is null)
                problems.Add($"Spark:Commerce:Merchant is ConnectedSellers, but provider '{main.Name}' cannot onboard sellers.");
            if (string.IsNullOrEmpty(options.PlatformOrganizationId))
                scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SparkCommercePaymentsExtensions))
                    .LogWarning("Spark:Commerce:Payments:PlatformOrganizationId is not set: no one sees the platform-wide dispute, fee and reconciliation queues.");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("Payments cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    private sealed class PaymentsMarker;
}
