using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Exports;
using MintPlayer.Spark.Commerce.Invoicing.Jobs;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;
using MintPlayer.Spark.Commerce.Invoicing.Reference;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Commerce.Invoicing.Vat;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Invoicing;

/// <summary>
/// Invoicing (<c>docs/commerce_PRD.md</c> §4.8, <c>docs/guide-commerce-invoicing.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddCommerceInvoicing()        // Spark:Commerce:Invoicing
///         .UseStorecovePeppol()           // or UseBillitPeppol(), UseTestPeppol(); optional
///         .UseReferenceDataVat();         // optional: TEDB suggestions and VAT territories (GC10, GA4)
/// });
/// </code>
/// </summary>
public static class SparkCommerceInvoicingExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkInvoicing"/> as Checkout's order-event recipient, the <c>Invoice</c> and
    /// <c>InvoiceTransmission</c> types with their read rights, the place-of-supply rules, VIES, the PDF renderer, the
    /// EN 16931 rule subset, the exports and the jobs (fee invoices, Peppol retry, retention). Adds Checkout.
    /// Safe to call more than once (each adapter calls it).
    /// </summary>
    public static SparkInvoicingBuilder AddCommerceInvoicing(this ISparkBuilder builder, Action<SparkCommerceInvoicingOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<InvoicingMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return new SparkInvoicingBuilder(builder);
        }

        builder.AddCommerceCheckout();

        services.AddSparkConfigurationSection<SparkCommerceInvoicingOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpContextAccessor();
        services.AddDataProtection();
        services.AddHttpClient(ViesVatIdValidator.HttpClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<InvoiceNumbering>();
        services.TryAddScoped<InvoiceIssuers>();
        services.TryAddScoped<IVatIdValidator, ViesVatIdValidator>();
        services.TryAddScoped<IVatReferenceSource, NoVatReferenceSource>();
        services.TryAddScoped<VatTerritories>();
        services.TryAddScoped<InvoiceLinks>();
        services.TryAddScoped<EInvoiceDispatcher>();
        services.TryAddScoped<InvoiceAfterIssue>();
        services.TryAddScoped<InvoicingAccess>();
        services.TryAddScoped<InvoicingExports>();
        services.TryAddScoped<LegalDocumentPdfs>();
        services.TryAddScoped<InvoicingService>();
        services.TryAddScoped<ISparkInvoicing>(sp => sp.GetRequiredService<InvoicingService>());
        services.TryAddSingleton<IInvoicePdfRenderer, InvoicePdfRenderer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEInvoiceValidator, En16931RuleSubset>());
        foreach (var rule in BuiltInPlaceOfSupplyRules.All)
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IPlaceOfSupplyRule), rule.GetType()));
        // Checkout's extension point (§4.1 coupling rule): Checkout never references Invoicing.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, InvoicingOrderEvents>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutMailAttachments, InvoicingCheckoutMailAttachments>());

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkCommerceInvoicingExtensions).Assembly, "SparkMail/");

        // Schedules are read at registration (the Cron registry needs them now): code, then configuration.
        var registrationTime = new SparkCommerceInvoicingOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkCommerceInvoicingOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron
            .AddJob<InvoiceTransmissionRetryJob>(registrationTime.TransmissionRetrySchedule, "Invoicing.TransmissionRetry")
            .AddJob<FeeInvoiceJob>(registrationTime.FeeInvoices.Schedule, "Invoicing.FeeInvoices")
            .AddJob<InvoiceRetentionJob>(registrationTime.RetentionSchedule, "Invoicing.Retention"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommerceInvoicingEndpoints());
        return new SparkInvoicingBuilder(builder);
    }

    /// <summary>Registers a Peppol access point adapter (for adapter packages).</summary>
    public static SparkInvoicingBuilder AddPeppolAccessPoint<TAccessPoint>(this ISparkBuilder builder)
        where TAccessPoint : class, IPeppolAccessPoint
    {
        var invoicing = builder.AddCommerceInvoicing();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IPeppolAccessPoint, TAccessPoint>());
        return invoicing;
    }

    /// <summary>
    /// GC10 / GA4: reads the TEDB rate suggestions and the country profiles' VAT territories that the reference-data
    /// client syncs into this application's database (<c>AddReferenceDataClient()</c> with its sync on). Suggestions are
    /// shown, never applied (§4.8).
    /// </summary>
    public static SparkInvoicingBuilder UseReferenceDataVat(this SparkInvoicingBuilder builder)
    {
        builder.Spark.Services.Replace(ServiceDescriptor.Scoped<IVatReferenceSource, SyncedReferenceDataVatSource>());
        return builder;
    }

    /// <summary>
    /// A misconfigured tax setup refuses startup: no categories, a default category that does not exist, a platform
    /// issuer without a name or VAT id, an unknown Peppol provider, an invalid time zone. Warns when the merchant mode
    /// is connected and DAC7 has no platform TIN.
    /// </summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SparkCommerceInvoicingOptions>>().Value;
        var commerce = scope.ServiceProvider.GetRequiredService<IOptions<SparkCommerceOptions>>().Value;
        var accessPoints = scope.ServiceProvider.GetServices<IPeppolAccessPoint>().ToList();
        var problems = Problems(options, commerce, accessPoints.Select(a => a.Name).ToList());
        if (problems.Count > 0)
            throw new InvalidOperationException("Invoicing cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        if (commerce.Merchant == MerchantMode.ConnectedSellers && string.IsNullOrEmpty(options.Dac7.PlatformTin))
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SparkCommerceInvoicingExtensions))
                .LogWarning("Spark:Commerce:Invoicing:Dac7:PlatformTin is not set: the DAC7 report (GA6) needs the platform operator's TIN.");
    }

    internal static List<string> Problems(SparkCommerceInvoicingOptions options, SparkCommerceOptions commerce, IReadOnlyList<string> accessPoints)
    {
        var problems = new List<string>();
        if (options.VatCategories.Count == 0)
            problems.Add("No VAT category is configured (Spark:Commerce:Invoicing:VatCategories): rates are the host's, nothing is hard-coded (§4.8).");
        else if (!options.VatCategories.ContainsKey(options.DefaultVatCategory))
            problems.Add($"Spark:Commerce:Invoicing:DefaultVatCategory names '{options.DefaultVatCategory}', which is not configured.");
        if (options.VatCategories.Count > 0 && !options.VatCategories.ContainsKey(options.FeeVatCategory))
            problems.Add($"Spark:Commerce:Invoicing:FeeVatCategory names '{options.FeeVatCategory}', which is not configured.");
        foreach (var (name, category) in options.VatCategories)
        {
            foreach (var (country, rate) in category.Rates)
            {
                if (rate is < 0 or > 10_000)
                    problems.Add($"VAT category '{name}': the rate for {country} ({rate} basis points) is out of range.");
            }
            if (category.Kind is VatCategoryKind.Exempt or VatCategoryKind.OutsideScope && string.IsNullOrEmpty(category.ExemptionReason) && string.IsNullOrEmpty(category.ExemptionReasonCode))
                problems.Add($"VAT category '{name}' is {category.Kind}: give an ExemptionReason or ExemptionReasonCode (Art. 226(11), BR-E-10/BR-O-10).");
        }
        if (commerce.Merchant == MerchantMode.Platform && (string.IsNullOrEmpty(options.Platform.Name) || string.IsNullOrEmpty(options.Platform.VatId)))
            problems.Add("The platform issues the invoices (Merchant = Platform): set Spark:Commerce:Invoicing:Platform:Name and :VatId (Art. 226(3), (5)).");
        if (options.Peppol.Provider is { Length: > 0 } provider && !accessPoints.Contains(provider, StringComparer.OrdinalIgnoreCase))
            problems.Add($"Spark:Commerce:Invoicing:Peppol:Provider names '{provider}', which is not registered.");
        if (options.Peppol.Provider is null && accessPoints.Count > 1)
            problems.Add("Several Peppol access points are registered: set Spark:Commerce:Invoicing:Peppol:Provider.");
        if (!MintPlayer.Spark.Services.SparkTimeZones.IsKnown(options.TimeZone))
            problems.Add($"Spark:Commerce:Invoicing:TimeZone '{options.TimeZone}' is not a known IANA zone.");
        if (options.Series.Digits is < 1 or > 12)
            problems.Add("Spark:Commerce:Invoicing:Series:Digits must be 1-12.");
        var prefixes = new[] { options.Series.Invoice, options.Series.Receipt, options.Series.CreditNote, options.Series.FeeInvoice, options.Series.FeeCreditNote };
        if (prefixes.Any(string.IsNullOrEmpty) || prefixes.Distinct(StringComparer.Ordinal).Count() != prefixes.Length)
            problems.Add("Spark:Commerce:Invoicing:Series: every prefix is set and distinct (a series is gap-free on its own).");
        return problems;
    }

    private sealed class InvoicingMarker;
}

/// <summary>The chain <c>AddCommerceInvoicing()</c> answers: adapters hang their <c>UseX()</c> on it.</summary>
public sealed class SparkInvoicingBuilder(ISparkBuilder spark)
{
    /// <summary>The Spark builder.</summary>
    public ISparkBuilder Spark { get; } = spark;
}
