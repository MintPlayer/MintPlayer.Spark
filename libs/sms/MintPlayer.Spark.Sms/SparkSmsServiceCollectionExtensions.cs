using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MintPlayer.Spark.Sms;

/// <summary>Registers an SMS adapter (<c>docs/commerce_PRD.md</c> X13).</summary>
/// <remarks>
/// Vendor adapters (Q-O1: which vendors is an open decision) register themselves the same way: one
/// <see cref="ISparkSmsSender"/>. The last registration wins, so a host that adds a vendor adapter after
/// the test adapter sends through the vendor.
/// </remarks>
public static class SparkSmsServiceCollectionExtensions
{
    /// <summary>
    /// Sends every text message to <see cref="SparkTestSmsSender"/>, which records it and delivers nothing:
    /// for tests and local development.
    /// </summary>
    public static IServiceCollection AddSparkTestSms(this IServiceCollection services)
    {
        services.TryAddSingleton<SparkTestSmsSender>();
        services.AddSingleton<ISparkSmsSender>(sp => sp.GetRequiredService<SparkTestSmsSender>());
        return services;
    }
}
