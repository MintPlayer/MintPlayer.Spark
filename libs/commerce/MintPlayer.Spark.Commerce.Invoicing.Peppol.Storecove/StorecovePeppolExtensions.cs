using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Invoicing.Peppol.Storecove;

/// <summary>Registration of the Storecove access point.</summary>
public static class StorecovePeppolExtensions
{
    /// <summary>
    /// Adds Storecove (<c>storecove</c>) as Peppol access point. Credentials: <c>Spark:Commerce:Invoicing:Peppol:Storecove</c>
    /// (user-secrets).
    /// </summary>
    public static SparkInvoicingBuilder UseStorecovePeppol(this SparkInvoicingBuilder invoicing, Action<StorecoveOptions>? configure = null)
    {
        var services = invoicing.Spark.Services;
        services.AddSparkConfigurationSection<StorecoveOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddHttpClient(StorecoveAccessPoint.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        return invoicing.Spark.AddPeppolAccessPoint<StorecoveAccessPoint>();
    }
}
