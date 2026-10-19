using MintPlayer.Spark.Abstractions.Builder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Webhooks;

[assembly: EndpointsMethodName("MapSparkTestShippingEndpoints")]

namespace MintPlayer.Spark.Commerce.Shipping.Test;

/// <summary>Registration of the in-process test carrier.</summary>
public static class TestShippingExtensions
{
    /// <summary>
    /// Adds the test carrier (<c>test</c>): every capability, its service points, the <see cref="TestCarrierSimulator"/>
    /// and <c>POST /spark/commerce/shipping/test/simulate</c>. Its webhooks use the real route
    /// <c>/spark/webhooks/shipping-test</c>, signed with a per-process key. Startup is refused in Production unless
    /// <c>Spark:Commerce:Shipping:Test:AllowInProduction</c> is set.
    /// </summary>
    public static SparkShippingBuilder UseTestCarrier(this SparkShippingBuilder shipping, Action<TestShippingOptions>? configure = null)
    {
        var builder = shipping.Spark;
        var services = builder.Services;
        services.AddSparkConfigurationSection<TestShippingOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TestCarrierStore>();
        services.TryAddScoped<TestCarrierSimulator>();

        builder.AddShippingCarrier<TestCarrier>(TestCarrier.CarrierName, source =>
        {
            source.Verification = sp => new HmacSignatureStrategy(
                new HmacSignatureScheme(TestCarrierSimulator.SignatureHeader),
                sp.GetRequiredService<TestCarrierStore>().WebhookSecret);
            source.DeliveryId = WebhookDeliveryIds.FromJsonProperty("id");
            source.ContentTypes.Add("application/json");
        });
        builder.AddServicePointProvider<TestCarrier>();

        builder.Registry.AddMiddleware(app =>
        {
            var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
            var options = app.ApplicationServices.GetRequiredService<IOptions<TestShippingOptions>>().Value;
            if (!TestShippingEndpoints.Enabled(environment, options))
                throw new InvalidOperationException("The test carrier is registered in Production: remove UseTestCarrier(), or set Spark:Commerce:Shipping:Test:AllowInProduction for a demo.");
        });
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkTestShippingEndpoints());
        return shipping;
    }
}

internal static class TestShippingEndpoints
{
    /// <summary>The simulator exists only outside Production, or when the host allows the test carrier there (a demo).</summary>
    public static bool Enabled(IHostEnvironment environment, TestShippingOptions options)
        => !environment.IsProduction() || options.AllowInProduction;
}

internal class TestShippingGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/shipping/test";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>
/// <c>POST /spark/commerce/shipping/test/simulate</c> (form: <c>trackingNumber</c>, <c>status</c> = <c>InTransit</c> |
/// <c>OutForDelivery</c> | <c>AvailableForPickup</c> | <c>Delivered</c> | <c>ReturnedToSender</c> | <c>Exception</c>,
/// optional <c>duplicate=true</c>): the simulated carrier scans a parcel. For E2E tests (GA1: a return's receipt).
/// </summary>
[MemberOf<TestShippingGroup>]
internal sealed partial class TestShippingSimulate : IPostEndpoint, IEndpointBase
{
    public static string Path => "/simulate";

    // A simulated carrier's own call, as a carrier's webhook would be: no SPA, no antiforgery token. It exists only
    // outside Production unless AllowInProduction.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly TestCarrierSimulator simulator;
    [Inject] private readonly IHostEnvironment environment;
    [Inject] private readonly IOptionsMonitor<TestShippingOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!TestShippingEndpoints.Enabled(environment, options.CurrentValue) || !httpContext.Request.HasFormContentType)
            return Results.NotFound();
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        if (!Enum.TryParse<TrackingStatus>(form["status"].ToString(), ignoreCase: false, out var status)
            || status is TrackingStatus.Pending or TrackingStatus.Labelled or TrackingStatus.Cancelled)
            return Results.BadRequest();
        var duplicate = string.Equals(form["duplicate"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        return await simulator.AdvanceAsync(form["trackingNumber"].ToString(), status, null, duplicate, httpContext.RequestAborted)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
