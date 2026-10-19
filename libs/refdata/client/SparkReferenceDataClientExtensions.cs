using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.ReferenceData.Client;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// A reference-data consumer (PRD §4.15, <c>docs/guide-reference-data-client.md</c>):
/// <code>
/// spark.AddReferenceDataClient();   // release sync, typed API client, Address behaviour, ISparkTimeZoneNameProvider
/// </code>
/// <c>Spark:ReferenceData:Client:BaseAddress</c> has no default: unset is free-text mode (RD-DEF 8).
/// </summary>
public static class SparkReferenceDataClientExtensions
{
    public static ISparkBuilder AddReferenceDataClient(this ISparkBuilder builder, Action<SparkReferenceDataClientOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<ReferenceDataClientMarker>())
        {
            if (configure is not null) services.Configure(configure);
            return builder;
        }

        services.AddSparkConfigurationSection<SparkReferenceDataClientOptions>();
        if (configure is not null)
            services.Configure(configure);

        var registrationTime = new SparkReferenceDataClientOptions();
        builder.Configuration?.GetSection(SparkReferenceDataClientOptions.SectionName).Bind(registrationTime);
        configure?.Invoke(registrationTime);

        services.AddHttpClient(ReferenceDataClient.HttpClientName, (sp, http) =>
            http.Timeout = sp.GetRequiredService<IOptions<SparkReferenceDataClientOptions>>().Value.Timeout);
        services.AddHttpClient(BestHouseNumberValidator.HttpClientName);
        services.TryAddSingleton<IReferenceDataClient, ReferenceDataClient>();
        // D36 step 1: the core's ISparkTimeZoneNames asks this provider first. Register at most one.
        services.TryAddSingleton<ISparkTimeZoneNameProvider, ReferenceDataTimeZoneNameProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHouseNumberValidator, BestHouseNumberValidator>());
        services.TryAddSingleton<ReleaseSyncService>();
        builder.AddInterceptor<AddressInterceptor>();

        if (registrationTime.Sync.Enabled)
            builder.AddCron(cron => cron.AddJob<ReleaseSyncJob>(registrationTime.Sync.Schedule, "RefData.Client.Sync"));

        builder.Registry.AddMiddleware(app => Validate(app.ApplicationServices));
        return builder;
    }

    /// <summary><c>BaseAddress</c>, when set, must be an absolute http(s) URL.</summary>
    internal static void Validate(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<SparkReferenceDataClientOptions>>().Value;
        if (options.BaseAddress is { } uri && (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Spark:ReferenceData:Client:BaseAddress must be an absolute http(s) URL (or unset for free-text mode).");
        if (options.Sync.Enabled && options.BaseAddress is null)
            throw new InvalidOperationException("Spark:ReferenceData:Client:Sync:Enabled needs a BaseAddress.");
    }

    private sealed class ReferenceDataClientMarker;
}
