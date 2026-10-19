using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Checkout.Jobs;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Inventory.Jobs;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>
/// Checkout (<c>docs/commerce_PRD.md</c> §4.4, <c>docs/guide-commerce-checkout.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMessaging();             // payment webhooks
///     spark.AddMolliePayments();        // or AddStripePayments(), AddTestPayments()
///     spark.AddCommerceCheckout();      // Spark:Commerce:Checkout; adds Inventory and Payments
/// });
/// </code>
/// Shop, Ticketing (or the host) register an <see cref="ICheckoutCapacitySource"/> for what they sell.
/// </summary>
public static class SparkCommerceCheckoutExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkCheckout"/>, the order type and its rights, the basket and back-office endpoints, the
    /// payment, lapsed-claim and visitor-merge hooks, the dispute evidence and buyers export, the donation and insurance
    /// pricing steps, the PaymentPending sweep and the abandoned-basket reminder. Declares the back-office organization
    /// role. Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddCommerceCheckout(this ISparkBuilder builder, Action<SparkCommerceCheckoutOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<CheckoutMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddCommerceInventory();
        builder.AddCommercePayments();

        services.AddSparkConfigurationSection<SparkCommerceCheckoutOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Values that decide what gets registered are read now: code, then configuration (config wins, §4.2).
        var registrationTime = new SparkCommerceCheckoutOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommerceCheckoutOptions.SectionName).Bind(registrationTime);
        builder.AddOrganizationRole(registrationTime.BackOfficeRole, "Creates phone and manual orders, comps and guest-list entries, re-sends confirmations and changes seats (GB7).");

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICheckoutBuyer, RequestCheckoutBuyer>();
        services.TryAddScoped<BasketStore>();
        services.TryAddScoped<CheckoutItems>();
        services.TryAddScoped<CheckoutAdmission>();
        services.TryAddScoped<CheckoutPricing>();
        services.TryAddScoped<CheckoutInsurance>();
        services.TryAddScoped<OrderEvents>();
        services.TryAddScoped<CheckoutMails>();
        services.TryAddScoped<OrderLifecycle>();
        services.TryAddScoped<OrderAccess>();
        services.TryAddScoped<BackOfficeOrders>();
        services.TryAddScoped<ISparkCheckout, CheckoutService>();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutPricingStep, DonationPricingStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutPricingStep, InsurancePricingStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPaymentEventRecipient, CheckoutPaymentRecipient>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IInventoryClaimLapsed, CheckoutClaimLapsed>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkVisitorMergeHandler, CheckoutBasketMerge>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDisputeEvidenceContributor, CheckoutDisputeEvidence>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISellerExportSource, CheckoutBuyersExport>());

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkCommerceCheckoutExtensions).Assembly, "SparkMail/");

        builder.AddCron(cron => cron
            .AddJob<CheckoutSweepJob>(registrationTime.SweepSchedule, "Checkout.PaymentPendingSweep")
            .AddJob<AbandonedBasketJob>(registrationTime.AbandonedBaskets.Schedule, "Checkout.AbandonedBaskets"));

        // M9: refunds, cancellations, withdrawal (GA1), guarantee claims (GA8), mass refunds (Refunds/).
        builder.AddCheckoutRefunds(registrationTime);

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommerceCheckoutEndpoints());
        return builder;
    }

    /// <summary>
    /// Refuses checkout settings that name an unknown channel or insurance provider, or a donation with nothing to offer;
    /// warns when nothing can be sold (no capacity source) or a gated phase could never be admitted (no admission gate).
    /// </summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<IOptions<SparkCommerceCheckoutOptions>>().Value;
        var commerce = sp.GetRequiredService<IOptions<SparkCommerceOptions>>().Value;
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(SparkCommerceCheckoutExtensions));
        var problems = new List<string>();
        const string root = "Spark:Commerce:Checkout";

        foreach (var name in options.Channels.Keys.Where(n => !commerce.Channels.ContainsKey(n)))
            problems.Add($"{root}:Channels:{name} names a channel that Spark:Commerce:Channels does not define.");
        foreach (var name in options.Insurance.Channels.Where(n => !commerce.Channels.ContainsKey(n)))
            problems.Add($"{root}:Insurance:Channels names '{name}', which Spark:Commerce:Channels does not define.");
        var insurers = sp.GetServices<IInsuranceProvider>().ToList();
        if (options.Insurance.Provider is { Length: > 0 } insurer && !insurers.Any(p => string.Equals(p.Name, insurer, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{root}:Insurance:Provider names '{insurer}', which is not registered.");
        if (options.Donation.Enabled && !options.Donation.RoundUp && options.Donation.FixedAmounts.Values.All(v => v.Count == 0))
            problems.Add($"{root}:Donation is enabled without fixed amounts or round-up.");
        if (options.RequiredAcceptance.Count == 0)
            logger?.LogWarning("{Root}:RequiredAcceptance is empty: buyers accept no terms at checkout (GA2, CRD Art. 6 and 8).", root);
        if (problems.Count > 0)
            throw new InvalidOperationException("Checkout cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));

        if (!sp.GetServices<ICheckoutCapacitySource>().Any())
            logger?.LogWarning("No ICheckoutCapacitySource is registered: nothing can be added to a basket. Shop, Ticketing.Events or the host register one.");
        if (!sp.GetServices<ICheckoutAdmissionGate>().Any())
            logger?.LogInformation("No ICheckoutAdmissionGate is registered: an item with an AdmissionScope is refused (fail closed) until the waiting room is added.");
    }

    private sealed class CheckoutMarker;
}
