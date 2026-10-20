using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Scanner;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Endpoints;

internal class TicketingTicketsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/ticketing/tickets";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>
/// Maps refusals: a ticket or checkout code as 409 with <see cref="SparkTicketRefusal"/>; "not yours" and "not found" as
/// the missing-row refusal (401 anonymous, 404 otherwise, #453); a validation error as 400.
/// </summary>
internal static class TicketResults
{
    public static async Task<IResult> RunAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<object?>> action)
    {
        try
        {
            var result = await action();
            return result switch
            {
                null => addOn.Refusal(http),
                false => addOn.Refusal(http),
                true => Results.NoContent(),
                IResult direct => direct,
                _ => addOn.Envelope(result, StatusCodes.Status200OK),
            };
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(http);
        }
        catch (TicketException ex) when (ex.Code is TicketRefusals.NotFound or TicketRefusals.Forbidden)
        {
            return addOn.Refusal(http);
        }
        catch (TicketException ex)
        {
            return addOn.Envelope(new SparkTicketRefusal(ex.Code, ex.Subject), StatusCodes.Status409Conflict);
        }
        catch (CheckoutException ex) when (ex.Code is CheckoutRefusals.Forbidden or CheckoutRefusals.OrderNotFound)
        {
            return addOn.Refusal(http);
        }
        catch (CheckoutException ex)
        {
            return addOn.Envelope(new SparkTicketRefusal(ex.Code, ex.Subject), StatusCodes.Status409Conflict);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
    }

    /// <summary>
    /// Runs a door-device call: the device behind <c>X-Spark-Scanner</c>, or 401 for a missing or wrong token. A revoked
    /// or expired device answers 409 <c>deviceRevoked</c>: the scanner wipes its offline list and queued scans.
    /// </summary>
    public static Task<IResult> DeviceAsync(ScannerDevices devices, ISparkAddOnEndpoints addOn, HttpContext http, Func<TicketScannerDevice, Task<object?>> action)
        => RunAsync(addOn, http, async () =>
        {
            var device = await devices.AuthenticateAsync(http.Request.Headers[ScannerDevices.HeaderName].ToString(), http.RequestAborted);
            if (device is null)
                return Results.Unauthorized();
            return await action(device);
        });

    public static string Query(HttpContext http, string name) => http.Request.Query[name].ToString();
}

// ---- the holder ---------------------------------------------------------------------------------------------------

/// <summary><c>GET /spark/ticketing/tickets/mine</c>: the tickets of the signed-in account, or of the guest visitor (X1).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class GetMyTickets : IGetEndpoint
{
    public static string Path => "/mine";

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => TicketResults.RunAsync(addOn, httpContext, async () => await tickets.GetMineAsync(httpContext.RequestAborted));
}

/// <summary><c>POST /spark/ticketing/tickets/open</c>: one ticket for its holder (the secret travels in the body, never in a URL).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class OpenTicket : IPostEndpoint<SparkTicketCredential>
{
    public static string Path => "/open";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketCredential request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.GetAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/pdf</c>: one ticket as a PDF (GC7 template) for its holder.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class DownloadTicketPdf : IPostEndpoint<SparkTicketCredential>
{
    public static string Path => "/pdf";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketCredential request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request is not null && await tickets.GetPdfAsync(request, cancellationToken) is { } bytes
                ? Results.File(bytes, "application/pdf", $"ticket-{request.Code.ToUpperInvariant()}.pdf")
                : null);
}

/// <summary><c>POST /spark/ticketing/tickets/holder</c>: names the holder of a named ticket (D30), until the freeze.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class SetTicketHolder : IPostEndpoint<SparkTicketHolderRequest>
{
    public static string Path => "/holder";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketHolderRequest request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.SetHolderAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/transfer</c>: offers the ticket to someone else (D30).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class StartTicketTransfer : IPostEndpoint<SparkTicketTransferRequest>
{
    public static string Path => "/transfer";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketTransferRequest request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.StartTransferAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/transfer/cancel</c>: withdraws a pending transfer.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class CancelTicketTransfer : IPostEndpoint<SparkTicketCredential>
{
    public static string Path => "/transfer/cancel";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketCredential request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is not null && await tickets.CancelTransferAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/transfer/offer</c>: what the recipient sees before accepting (the token from the link's fragment).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class GetTicketTransferOffer : IPostEndpoint<SparkTicketTransferAccept>
{
    public static string Path => "/transfer/offer";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketTransferAccept request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.GetTransferOfferAsync(request.TransferId, request.Token, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/transfer/accept</c>: takes the ticket over (new secret; the giver's data erased, §4.11).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class AcceptTicketTransfer : IPostEndpoint<SparkTicketTransferAccept>
{
    public static string Path => "/transfer/accept";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketTransferAccept request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.AcceptTransferAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/resale</c>: lists the ticket on the face-value exchange (D30).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class ListTicketForResale : IPostEndpoint<SparkTicketResaleRequest>
{
    public static string Path => "/resale";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketResaleRequest request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.ListForResaleAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/resale/withdraw</c>: withdraws an open listing.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class WithdrawTicketResale : IPostEndpoint<SparkTicketCredential>
{
    public static string Path => "/resale/withdraw";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketCredential request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is not null && await tickets.WithdrawResaleAsync(request, cancellationToken));
}

/// <summary><c>GET /spark/ticketing/tickets/resale/offers?eventId=</c>: a published event's open listings. Anonymous.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class GetResaleOffers : IGetEndpoint
{
    public static string Path => "/resale/offers";

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => TicketResults.RunAsync(addOn, httpContext, async () => await tickets.GetResaleOffersAsync(TicketResults.Query(httpContext, "eventId"), httpContext.RequestAborted));
}

// ---- the seller ---------------------------------------------------------------------------------------------------

/// <summary><c>POST /spark/ticketing/tickets/resend</c>: re-sends an order's tickets, or one ticket (GB7). The seller's members.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class ResendTickets : IPostEndpoint<SparkTicketResendRequest>
{
    public static string Path => "/resend";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkTicketResendRequest request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.ResendAsync(request, cancellationToken));
}

/// <summary>
/// <c>POST /spark/ticketing/tickets/template-image?eventId=&amp;kind=logo|background</c>: the event's ticket logo or
/// background (GC7), a JPEG body of at most <c>Delivery:MaxTemplateImageBytes</c>. The seller's members.
/// </summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class UploadTicketTemplateImage : IPostEndpoint
{
    public static string Path => "/template-image";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => TicketResults.RunAsync(addOn, httpContext, async () => await tickets.SetTemplateImageAsync(
            TicketResults.Query(httpContext, "eventId"), TicketResults.Query(httpContext, "kind"), httpContext.Request.Body, httpContext.Request.ContentLength ?? 0, httpContext.RequestAborted));
}

/// <summary><c>GET /spark/ticketing/tickets/template-image?eventId=&amp;kind=</c>: an event's ticket logo or background (GC7). Anonymous: it is the event's branding.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class GetTicketTemplateImage : IGetEndpoint
{
    public static string Path => "/template-image";

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var bytes = await tickets.GetTemplateImageAsync(TicketResults.Query(httpContext, "eventId"), TicketResults.Query(httpContext, "kind"), httpContext.RequestAborted);
        if (bytes is null)
            return addOn.Refusal(httpContext);
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(bytes, "image/jpeg");
    }
}

/// <summary><c>POST /spark/ticketing/tickets/scanner/devices</c>: registers a door device (the <c>scanner</c> role, X8); the token is shown once.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class RegisterScannerDevice : IPostEndpoint<SparkScannerDeviceRequest>
{
    public static string Path => "/scanner/devices";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkScannerDeviceRequest request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await tickets.RegisterDeviceAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/scanner/devices/revoke</c>: revokes a door device; it wipes its list on its next call.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class RevokeScannerDevice : IPostEndpoint<SparkScannerDeviceRevokeRequest>
{
    public static string Path => "/scanner/devices/revoke";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTickets tickets;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkScannerDeviceRevokeRequest request, CancellationToken cancellationToken)
        => TicketResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is not null && await tickets.RevokeDeviceAsync(request.DeviceId, cancellationToken));
}

// ---- the door device (X-Spark-Scanner) ------------------------------------------------------------------------------
// The device token is a header a cross-site page cannot make the browser send, and these endpoints read no cookie: no
// ambient credential, so no antiforgery token (SparkAntiforgeryMiddleware, F8).

/// <summary><c>GET /spark/ticketing/tickets/scanner/session</c>: the device's event, list, dates, PBKDF2 cost and wipe time.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class GetScannerSession : IGetEndpoint
{
    public static string Path => "/scanner/session";

    [Inject] private readonly ScannerDevices devices;
    [Inject] private readonly TicketCheckIns checkIns;
    [Inject] private readonly TicketPresenter presenter;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => TicketResults.DeviceAsync(devices, addOn, httpContext, async device => await checkIns.SessionAsync(device, presenter, httpContext.RequestAborted));
}

/// <summary><c>GET /spark/ticketing/tickets/scanner/list?next=</c>: a page of the offline list (S9): PBKDF2 verifiers, never secrets.</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class GetScannerListPage : IGetEndpoint
{
    public static string Path => "/scanner/list";

    [Inject] private readonly ScannerDevices devices;
    [Inject] private readonly TicketCheckIns checkIns;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var next = TicketResults.Query(httpContext, "next");
        return TicketResults.DeviceAsync(devices, addOn, httpContext, async device => await checkIns.ListPageAsync(device, string.IsNullOrEmpty(next) ? null : next, httpContext.RequestAborted));
    }
}

/// <summary><c>POST /spark/ticketing/tickets/scanner/scan</c>: an online scan, marked atomically per check-in list (GB3).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class ScanTicket : IPostEndpoint<SparkScanRequest>
{
    public static string Path => "/scanner/scan";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.DisableAntiforgery();

    [Inject] private readonly ScannerDevices devices;
    [Inject] private readonly TicketCheckIns checkIns;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkScanRequest request, CancellationToken cancellationToken)
        => TicketResults.DeviceAsync(devices, addOn, httpContextAccessor.HttpContext!, async device => request is null ? null : await checkIns.ScanAsync(device, request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/tickets/scanner/sync</c>: uploads offline scans; answers the double entries and the list's changes (S9).</summary>
[MemberOf<TicketingTicketsGroup>]
internal sealed partial class SyncScanner : IPostEndpoint<SparkScannerSyncRequest>
{
    public static string Path => "/scanner/sync";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.DisableAntiforgery();

    [Inject] private readonly ScannerDevices devices;
    [Inject] private readonly TicketCheckIns checkIns;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkScannerSyncRequest request, CancellationToken cancellationToken)
        => TicketResults.DeviceAsync(devices, addOn, httpContextAccessor.HttpContext!, async device => request is null ? null : await checkIns.SyncAsync(device, request, cancellationToken));
}
