using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Test;

/// <summary>Registration of the in-process test provider.</summary>
public static class TestPaymentsExtensions
{
    /// <summary>
    /// Adds the test provider (<c>test</c>): its payment page at <c>/spark/commerce/payments/test/pay</c>, the
    /// <see cref="TestPaymentSimulator"/>, test Connect onboarding and test settlements. Its webhooks use the real route
    /// <c>/spark/webhooks/payments-test</c>, signed with a per-process key. Startup is refused in Production unless
    /// <c>Spark:Commerce:Payments:Test:AllowInProduction</c> is set.
    /// </summary>
    public static ISparkBuilder AddTestPayments(this ISparkBuilder builder, Action<TestPaymentsOptions>? configure = null)
    {
        var services = builder.Services;
        services.AddSparkConfigurationSection<TestPaymentsOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TestPaymentStore>();
        services.TryAddScoped<TestPaymentProvider>();
        services.TryAddScoped(sp => new TestPaymentSimulator(
            sp.GetRequiredService<TestPaymentStore>(),
            sp.GetRequiredService<TestPaymentProvider>(),
            sp.GetRequiredService<IWebhookReceiver>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConnectOnboardingProvider, TestConnect>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISettlementSource, TestSettlementSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommercePaymentCapabilities, TestCapabilities>());

        builder.AddPaymentProvider<TestPaymentProvider>(TestPaymentProvider.ProviderName, source =>
        {
            source.Verification = sp => new HmacSignatureStrategy(
                new HmacSignatureScheme(TestPaymentSimulator.SignatureHeader),
                sp.GetRequiredService<TestPaymentStore>().WebhookSecret);
            source.DeliveryId = WebhookDeliveryIds.FromJsonProperty("id");
            source.ContentTypes.Add("application/json");
        });

        builder.Registry.AddMiddleware(app =>
        {
            var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
            var options = app.ApplicationServices.GetRequiredService<IOptions<TestPaymentsOptions>>().Value;
            if (!TestPaymentEndpoints.Enabled(environment, options))
                throw new InvalidOperationException("The test payment provider is registered in Production: remove AddTestPayments(), or set Spark:Commerce:Payments:Test:AllowInProduction for a demo.");
        });
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkTestPaymentsEndpoints());
        return builder;
    }

    private sealed class TestCapabilities(IOptionsMonitor<TestPaymentsOptions> options) : ICommercePaymentCapabilities
    {
        public string Provider => TestPaymentProvider.ProviderName;

        public bool SupportsSplit => options.CurrentValue.SupportsSplit;
    }
}
