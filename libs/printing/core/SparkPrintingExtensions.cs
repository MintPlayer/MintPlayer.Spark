using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Printing.Services;

namespace MintPlayer.Spark.Printing;

/// <summary>
/// Printing (<c>docs/warehouse_picking_PRD.md</c> §4.11, WM6):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;AppUser&gt;();   // registers the personal-access-token scheme the agents use (Q-W14)
///     spark.AddPrinting();                    // Spark:Printing (Printers, Agents)
///     spark.AddPrintingQzTray();              // optional bridges
/// });
/// // App_Data/security.json: "bindings": { "printing:operators": [ … ], "printing:administrators": [ … ] }
/// </code>
/// </summary>
public static class SparkPrintingExtensions
{
    /// <summary>
    /// Registers the job queue, the router, the agents' and stations' endpoints (<c>/spark/printing/*</c>) and the built-in
    /// adapters (<c>Pdf</c>, <c>Agent</c>); binds <c>Spark:Printing</c> and refuses startup on an invalid value (an
    /// unregistered adapter among them). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddPrinting(this ISparkBuilder builder, Action<SparkPrintingOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<PrintingMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        services.AddSparkConfigurationSection<SparkPrintingOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSingleton<IValidateOptions<SparkPrintingOptions>, SparkPrintingOptionsValidator>();
        services.AddOptions<SparkPrintingOptions>().ValidateOnStart();

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);

        builder.AddPrinterAdapter(PrinterAdapters.Pdf, deliveredByStation: true);
        builder.AddPrinterAdapter(PrinterAdapters.Agent, deliveredByStation: false);

        services.TryAddSingleton<IPrintRouter, ConfiguredPrintRouter>();
        services.TryAddScoped<IPrintJobs, PrintJobs>();
        services.TryAddScoped<IPrintAgents, PrintAgents>();

        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkPrintingEndpoints());
        return builder;
    }

    /// <summary>
    /// Registers a printer adapter by name (the bridge packages call this): a printer configured with it passes startup
    /// validation. <paramref name="deliveredByStation"/>: the station's browser fetches and acknowledges its jobs.
    /// </summary>
    public static ISparkBuilder AddPrinterAdapter(this ISparkBuilder builder, string name, bool deliveredByStation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Not TryAddEnumerable: every registration is the same implementation type, which it would deduplicate away.
        var registered = builder.Services.Any(d => d.ServiceType == typeof(PrinterAdapterRegistration)
            && d.ImplementationInstance is PrinterAdapterRegistration r && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!registered)
            builder.Services.AddSingleton(new PrinterAdapterRegistration(name, deliveredByStation));
        return builder;
    }

    private sealed class PrintingMarker;
}
