using MintPlayer.Spark.Abstractions.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping.Sendcloud;

/// <summary>Registration of the Sendcloud adapter.</summary>
public static class SendcloudShippingExtensions
{
    /// <summary>The header Sendcloud signs its webhooks in: lower-case hex HMAC-SHA256 of the raw body.</summary>
    public const string SignatureHeader = "Sendcloud-Signature";

    /// <summary>
    /// Adds Sendcloud (<c>sendcloud</c>): parcels and labels, service points, and the signed
    /// <c>parcel_status_changed</c> webhook at <c>/spark/webhooks/shipping-sendcloud</c> (verified against the default
    /// account's secret key and every named account's). Credentials: <c>Spark:Commerce:Shipping:Sendcloud</c> (user-secrets).
    /// </summary>
    public static SparkShippingBuilder UseSendcloud(this SparkShippingBuilder shipping, Action<SendcloudOptions>? configure = null)
    {
        var builder = shipping.Spark;
        var services = builder.Services;
        services.AddSparkConfigurationSection<SendcloudOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddHttpClient(SendcloudApi.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddScoped<SendcloudApi>();
        builder.AddServicePointProvider<SendcloudCarrier>();
        builder.AddShippingCarrier<SendcloudCarrier>(SendcloudCarrier.CarrierName, source =>
        {
            source.Verification = sp => Verification(sp.GetRequiredService<IOptionsMonitor<SendcloudOptions>>().CurrentValue);
            source.ContentTypes.Add("application/json");
            source.MaxBodyBytes = 256 * 1024;
            // No delivery id: Sendcloud sends none. Events are deduplicated by their digest when recorded.
        });
        return shipping;
    }

    /// <summary>Accepts a delivery signed with any configured account's secret key; fail-closed when none is set.</summary>
    internal static IWebhookSignatureStrategy Verification(SendcloudOptions options)
    {
        var secrets = new[] { options.SecretKey }
            .Concat(options.Accounts.Values.Select(a => a.SecretKey))
            .Where(s => s is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new AnyOfStrategy(secrets.Count == 0
            ? [new HmacSignatureStrategy(new HmacSignatureScheme(SignatureHeader), null)]
            : secrets.Select(s => (IWebhookSignatureStrategy)new HmacSignatureStrategy(new HmacSignatureScheme(SignatureHeader), s)).ToList());
    }

    private sealed class AnyOfStrategy(IReadOnlyList<IWebhookSignatureStrategy> strategies) : IWebhookSignatureStrategy
    {
        public WebhookVerification Verify(WebhookRequest request)
        {
            WebhookVerification? last = null;
            foreach (var strategy in strategies)
            {
                last = strategy.Verify(request);
                if (last.IsAccepted)
                    return last;
            }
            return last!;
        }
    }
}
