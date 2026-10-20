using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Endpoints;

[assembly: EndpointsMethodName("MapSparkPrintingZebraEndpoints")]

namespace MintPlayer.Spark.Printing.ZebraBrowserPrint;

/// <summary>
/// <c>Spark:Printing:ZebraBrowserPrint</c> (Q-W15): Zebra's Browser Print library is "available on request" and its licence
/// is unverified, so it is never bundled; the host serves it and names its URL here. Check the licence before release.
/// </summary>
public sealed class SparkZebraBrowserPrintOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Printing:ZebraBrowserPrint";

    /// <summary>The URL of Zebra's <c>BrowserPrint-*.min.js</c>, as the host serves it (https, or a path on the app).</summary>
    public string? LibraryUrl { get; set; }
}

internal sealed class SparkZebraBrowserPrintOptionsValidator : IValidateOptions<SparkZebraBrowserPrintOptions>
{
    public ValidateOptionsResult Validate(string? name, SparkZebraBrowserPrintOptions options)
        => IsAllowed(options.LibraryUrl)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Spark:Printing:ZebraBrowserPrint:LibraryUrl must be an https URL or a path on the app (/…): the host supplies Zebra's library (Q-W15).");

    /// <summary>An absolute https URL, or an app-relative path; never http to another host, never another scheme.</summary>
    internal static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
            return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
}

public static class SparkPrintingZebraBrowserPrintExtensions
{
    /// <summary>
    /// Registers the <c>ZebraBrowserPrint</c> printer adapter (delivered by the station's browser through Zebra's local
    /// service) and <c>GET /spark/printing/zebra/settings</c>; refuses startup without the library URL.
    /// </summary>
    public static ISparkBuilder AddPrintingZebraBrowserPrint(this ISparkBuilder builder, Action<SparkZebraBrowserPrintOptions>? configure = null)
    {
        builder.AddPrinting();
        var services = builder.Services;
        if (!services.TryAddSparkMarker<ZebraMarker>())
            return builder;
        services.AddSparkConfigurationSection<SparkZebraBrowserPrintOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSingleton<IValidateOptions<SparkZebraBrowserPrintOptions>, SparkZebraBrowserPrintOptionsValidator>();
        services.AddOptions<SparkZebraBrowserPrintOptions>().ValidateOnStart();
        builder.AddPrinterAdapter(PrinterAdapters.ZebraBrowserPrint, deliveredByStation: true);
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkPrintingZebraEndpoints());
        return builder;
    }

    private sealed class ZebraMarker;
}

internal class ZebraGroup : IEndpointGroup
{
    public static string Prefix => "/spark/printing/zebra";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.RequireAuthorization();
}

/// <summary>What the station's browser needs to load Zebra's library.</summary>
[SparkContract]
public sealed record ZebraBrowserPrintSettings(string LibraryUrl);

/// <summary><c>GET /spark/printing/zebra/settings</c> (station operators only).</summary>
[MemberOf<ZebraGroup>]
internal sealed partial class GetZebraBrowserPrintSettings : IGetEndpoint
{
    public static string Path => "/settings";

    [Inject] private readonly IOptions<SparkZebraBrowserPrintOptions> options;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => await permissions.IsAllowedAsync("Read", "PrintJob", httpContext.RequestAborted)
            ? Results.Ok(new ZebraBrowserPrintSettings(options.Value.LibraryUrl!))
            : addOn.Refusal(httpContext);
}
