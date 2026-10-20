using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Stripe;

/// <summary>Registration of the Stripe adapter.</summary>
public static class StripePaymentsExtensions
{
    /// <summary>The Connect endpoint's webhook source: connected accounts' events, signed with their own secret.</summary>
    public const string ConnectWebhookSource = "payments-stripe-connect";

    /// <summary>
    /// Adds Stripe as a payment provider (<c>stripe</c>): Checkout Sessions, the signed webhooks at
    /// <c>/spark/webhooks/payments-stripe</c> and <c>/spark/webhooks/payments-stripe-connect</c> (deduplicated on the event
    /// id <c>evt_…</c>), Connect onboarding and payouts. Keys: <c>Spark:Commerce:Payments:Stripe</c> (user-secrets).
    /// </summary>
    public static ISparkBuilder AddStripePayments(this ISparkBuilder builder, Action<StripePaymentsOptions>? configure = null)
    {
        var services = builder.Services;
        services.AddSparkConfigurationSection<StripePaymentsOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddHttpClient(StripeApi.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<StripeApi>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConnectOnboardingProvider, StripeConnect>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISettlementSource, StripeSettlementSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommercePaymentCapabilities, StripeCapabilities>());

        builder.AddPaymentProvider<StripePaymentProvider>(StripePaymentProvider.ProviderName, source => Configure(source, o => o.WebhookSecret));
        return builder.AddPaymentWebhookSource(ConnectWebhookSource, source => Configure(source, o => o.ConnectWebhookSecret));
    }

    private static void Configure(WebhookSourceBuilder source, Func<StripePaymentsOptions, string?> secret)
    {
        // The secret is read per delivery, so a rotated one takes effect without a restart.
        source.Verification = services =>
        {
            var options = services.GetRequiredService<IOptionsMonitor<StripePaymentsOptions>>().CurrentValue;
            return new StripeSignatureStrategy(secret(options), options.WebhookTolerance, services.GetService<TimeProvider>());
        };
        source.DeliveryId = WebhookDeliveryIds.FromJsonProperty("id");
        source.ContentTypes.Add("application/json");
        source.MaxBodyBytes = 512 * 1024;
    }

    /// <summary>What Commerce's startup validation reads (D40: split capability).</summary>
    private sealed class StripeCapabilities(IOptionsMonitor<StripePaymentsOptions> options) : ICommercePaymentCapabilities
    {
        public string Provider => StripePaymentProvider.ProviderName;

        public bool SupportsSplit => options.CurrentValue.SeparateChargesAndTransfers;

        /// <summary>Checkout Sessions are a redirect: no Stripe script loads on the host's pages, so no consent purpose (GA9).</summary>
        public string? RequiresConsentPurpose => null;
    }
}
