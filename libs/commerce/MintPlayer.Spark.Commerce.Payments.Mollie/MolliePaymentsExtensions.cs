using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Mollie;

/// <summary>Registration of the Mollie adapter.</summary>
public static class MolliePaymentsExtensions
{
    /// <summary>
    /// Adds Mollie as a payment provider (<c>mollie</c>): payments, the fetch-by-id webhook at
    /// <c>/spark/webhooks/payments-mollie</c> (never deduplicated on the id: Mollie sends the same id for every change, X5),
    /// Mollie Connect onboarding and settlements. Credentials: <c>Spark:Commerce:Payments:Mollie</c> (user-secrets).
    /// </summary>
    public static ISparkBuilder AddMolliePayments(this ISparkBuilder builder, Action<MolliePaymentsOptions>? configure = null)
    {
        var services = builder.Services;
        services.AddSparkConfigurationSection<MolliePaymentsOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddHttpClient(MollieApi.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<MollieApi>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConnectOnboardingProvider, MollieConnect>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISettlementSource, MollieSettlementSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommercePaymentCapabilities, MollieCapabilities>());

        return builder.AddPaymentProvider<MolliePaymentProvider>(MolliePaymentProvider.ProviderName, source =>
        {
            source.Verification = _ => new FetchByIdStrategy();
            // Mollie posts application/x-www-form-urlencoded "id=tr_…"; FetchByIdStrategy reads both forms and JSON.
            source.MaxBodyBytes = 4 * 1024;
            // No DeliveryId: deduplicating on the id would drop every status change after the first.
        });
    }

    /// <summary>What Commerce's startup validation reads (D40: split capability).</summary>
    private sealed class MollieCapabilities(IOptionsMonitor<MolliePaymentsOptions> options) : ICommercePaymentCapabilities
    {
        public string Provider => MolliePaymentProvider.ProviderName;

        public bool SupportsSplit => options.CurrentValue.SplitPayments;
    }
}
