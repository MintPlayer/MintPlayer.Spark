using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Shipping.Jobs;
using MintPlayer.Spark.Commerce.Shipping.Rates;
using MintPlayer.Spark.Commerce.Shipping.Services;
using MintPlayer.Spark.Commerce.Shipping.Webhooks;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>
/// Shipping (<c>docs/commerce_PRD.md</c> §4.9, <c>docs/guide-commerce-shipping.md</c>). A shop host and a warehouse
/// host register it the same way:
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMessaging();                 // carrier webhooks are handled from the spark-webhooks queue
///     spark.AddCommerceShipping()           // Spark:Commerce:Shipping
///         .UseSendcloud()                   // or UseBpost(), UsePostNL(), UseTestCarrier(); several are allowed
///         .UseReferenceDataHolidays();      // optional (GC11)
/// });
/// </code>
/// </summary>
public static class SparkCommerceShippingExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkShipping"/>, the configured methods as a rate provider, the dispatch schedule, the
    /// <c>Shipment</c> type with its rights (the <c>commerce-shipping:staff</c> slot), the tracking poll and the
    /// service-point and rate endpoints. Safe to call more than once (each adapter calls it).
    /// </summary>
    public static SparkShippingBuilder AddCommerceShipping(this ISparkBuilder builder, Action<SparkCommerceShippingOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<ShippingMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return new SparkShippingBuilder(builder);
        }

        services.AddSparkConfigurationSection<SparkCommerceShippingOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ShippingCarrierRegistry>();
        services.TryAddScoped<DispatchSchedule>();
        services.TryAddScoped<SparkShipping>();
        services.TryAddScoped<ISparkShipping>(sp => sp.GetRequiredService<SparkShipping>());
        // Configuration's methods come first: GetRatesAsync keeps the providers' order.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IShippingRateProvider, ConfiguredShippingRateProvider>());

        // The poll's schedule is read at registration (the Cron registry needs it now): code, then configuration.
        var registrationTime = new SparkCommerceShippingOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommerceShippingOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron.AddJob<TrackingPollJob>(registrationTime.TrackingPollSchedule, "Shipping.TrackingPoll"));
        builder.AddCron(cron => cron.AddJob<BookingReconcileJob>(registrationTime.BookingReconcileSchedule, "Shipping.BookingReconcile"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommerceShippingEndpoints());
        return new SparkShippingBuilder(builder);
    }

    /// <summary>
    /// Registers a carrier adapter (for adapter packages). With <paramref name="configureWebhook"/>, the carrier's
    /// tracking webhook source <c>shipping-{name}</c> is added (X5), handled from the queue; carriers without webhooks
    /// are polled instead.
    /// </summary>
    public static SparkShippingBuilder AddShippingCarrier<TCarrier>(this ISparkBuilder builder, string name, Action<WebhookSourceBuilder>? configureWebhook = null)
        where TCarrier : class, IShippingCarrier
    {
        var shipping = builder.AddCommerceShipping();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IShippingCarrier, TCarrier>());
        if (configureWebhook is not null)
        {
            builder.AddWebhookSource(ShippingWebhookSources.For(name), source =>
            {
                configureWebhook(source);
                source.UseHandler<ShippingWebhookHandler>();
            });
        }
        return shipping;
    }

    /// <summary>Registers a carrier's service-point search (GB5).</summary>
    public static SparkShippingBuilder AddServicePointProvider<TProvider>(this ISparkBuilder builder)
        where TProvider : class, IServicePointProvider
    {
        var shipping = builder.AddCommerceShipping();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IServicePointProvider, TProvider>());
        return shipping;
    }

    /// <summary>
    /// Skips public holidays from the reference data's calendar (GC11) in cut-offs and delivery promises. The calendar
    /// is the reference-data client's sync (<c>PublicHolidays</c>); without it, no day is a holiday.
    /// </summary>
    public static SparkShippingBuilder UseReferenceDataHolidays(this SparkShippingBuilder builder)
    {
        builder.Spark.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IShippingHolidayCalendar, ReferenceDataHolidayCalendar>());
        return builder;
    }

    /// <summary>
    /// Refuses startup on: no carrier, two carriers of one name, an ambiguous or unknown default carrier, a method
    /// that names an unknown carrier, service or zone, an unknown currency, or an unknown dispatch time zone.
    /// </summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var carriers = scope.ServiceProvider.GetServices<IShippingCarrier>().ToList();
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<SparkCommerceShippingOptions>>().Value;
        var problems = Problems(carriers, settings);
        if (problems.Count > 0)
            throw new InvalidOperationException("Shipping cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    internal static List<string> Problems(IReadOnlyList<IShippingCarrier> carriers, SparkCommerceShippingOptions settings)
    {
        var problems = new List<string>();
        if (carriers.Count == 0)
            problems.Add("No carrier is registered: add UseSendcloud(), UseBpost(), UsePostNL() or UseTestCarrier().");
        foreach (var duplicate in carriers.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Two carriers are named '{duplicate.Key}'.");
        if (carriers.Count > 1 && string.IsNullOrEmpty(settings.Carrier))
            problems.Add("Several carriers are registered: set Spark:Commerce:Shipping:Carrier.");
        if (settings.Carrier is { Length: > 0 } configured && !carriers.Any(c => string.Equals(c.Name, configured, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"Spark:Commerce:Shipping:Carrier names '{configured}', which is not registered.");
        if (!Money.IsKnownCurrency(settings.Currency))
            problems.Add($"Spark:Commerce:Shipping:Currency '{settings.Currency}' is not an ISO 4217 currency.");
        if (!SparkTimeZones.IsKnown(settings.Dispatch.TimeZone))
            problems.Add($"Spark:Commerce:Shipping:Dispatch:TimeZone '{settings.Dispatch.TimeZone}' is not a known IANA zone.");

        foreach (var (zoneName, zone) in settings.Zones)
        {
            foreach (var country in zone.Countries.Where(c => c != "*" && !ShippingAddressRules.IsCountryCode(c)))
                problems.Add($"Zone '{zoneName}' lists '{country}', which is not an ISO 3166-1 alpha-2 code.");
        }
        foreach (var (key, method) in settings.Methods)
        {
            foreach (var zone in method.Rates.Keys.Concat(method.FreeFrom.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(z => !settings.Zones.ContainsKey(z)))
                problems.Add($"Method '{key}' prices zone '{zone}', which is not configured.");
            if (method.Kind == ShippingMethodKind.Pickup)
                continue;
            var carrierName = method.Carrier ?? settings.Carrier ?? (carriers.Count == 1 ? carriers[0].Name : null);
            var carrier = carriers.FirstOrDefault(c => string.Equals(c.Name, carrierName, StringComparison.OrdinalIgnoreCase));
            if (carrier is null)
                problems.Add($"Method '{key}' names carrier '{carrierName}', which is not registered.");
            else if (method.Service is not { Length: > 0 } service || !carrier.Services.Any(s => string.Equals(s.Code, service, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"Method '{key}' names service '{method.Service}', which {carrier.Name} does not offer.");
        }
        return problems;
    }

    private sealed class ShippingMarker;
}

/// <summary>The chain <c>AddCommerceShipping()</c> answers: adapters hang their <c>UseX()</c> on it.</summary>
public sealed class SparkShippingBuilder(ISparkBuilder spark)
{
    /// <summary>The Spark builder.</summary>
    public ISparkBuilder Spark { get; } = spark;
}
