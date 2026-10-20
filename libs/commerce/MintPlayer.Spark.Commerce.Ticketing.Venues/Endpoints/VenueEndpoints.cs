using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Live;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Endpoints;

// /spark/ticketing/venues/*. Ids travel in query strings and bodies, never in paths (RavenDB ids contain slashes).
// Every refusal is the uniform missing-row answer (#453): an id is no oracle.

internal class VenuesGroup : IEndpointGroup
{
    public static string Prefix => "/spark/ticketing/venues";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>Runs a layout operation and maps its exceptions: refusal → 404/401, stale → 409, problems → 400 with the list.</summary>
internal static class VenueResults
{
    public static async Task<IResult> RunAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SparkAccessDeniedException) { return addOn.Refusal(http); }
        catch (VenueLayoutConflictException ex) { return addOn.Envelope(new VenueLayoutSaveResponse(ex.CurrentRevision, []), StatusCodes.Status409Conflict); }
        catch (VenueLayoutInvalidException ex) { return addOn.Envelope(new VenueLayoutSaveResponse(0, Problems(ex.Problems)), StatusCodes.Status400BadRequest); }
        catch (SparkValidationException ex) { return addOn.ValidationFailed(ex); }
    }

    public static IReadOnlyList<VenueLayoutProblem> Problems(IReadOnlyList<LayoutProblem> problems)
        => problems.Select(p => new VenueLayoutProblem(p.Path, p.Code)).ToList();
}

/// <summary><c>POST /spark/ticketing/venues/layouts/create</c>: a new private layout of a venue (D8).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class CreateVenueLayout : IPostEndpoint<VenueLayoutCreateRequest>
{
    public static string Path => "/layouts/create";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(VenueLayoutCreateRequest request, CancellationToken cancellationToken)
        => VenueResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            addOn.Envelope(await venues.CreateLayoutAsync(request?.VenueId ?? "", request?.Name ?? "", cancellationToken), StatusCodes.Status201Created));
}

/// <summary><c>POST /spark/ticketing/venues/layouts/copy</c>: copy a layout to edit (D8).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class CopyVenueLayout : IPostEndpoint<VenueLayoutCopyRequest>
{
    public static string Path => "/layouts/copy";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(VenueLayoutCopyRequest request, CancellationToken cancellationToken)
        => VenueResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            addOn.Envelope(await venues.CopyLayoutAsync(request?.LayoutId ?? "", request?.Name ?? "", cancellationToken), StatusCodes.Status201Created));
}

/// <summary><c>GET /spark/ticketing/venues/layouts/plan?layoutId=…&amp;version=draft|proposal|published|{n}</c>.</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class GetVenueLayoutPlan : IGetEndpoint
{
    public static string Path => "/layouts/plan";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => VenueResults.RunAsync(addOn, httpContext, async () =>
        {
            var query = httpContext.Request.Query;
            var version = query["version"].ToString() is { Length: > 0 } v ? v : "draft";
            return addOn.Envelope(await venues.GetPlanAsync(query["layoutId"].ToString(), version, httpContext.RequestAborted), StatusCodes.Status200OK);
        });
}

/// <summary><c>POST /spark/ticketing/venues/layouts/draft</c>: save the editor's draft (owner; 409 on a stale revision).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class SaveVenueLayoutDraft : IPostEndpoint<VenueLayoutSaveDraftRequest>
{
    public static string Path => "/layouts/draft";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(VenueLayoutSaveDraftRequest request, CancellationToken cancellationToken)
        => VenueResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.Plan is null)
                return addOn.Refusal(httpContextAccessor.HttpContext!);
            var revision = await venues.SaveDraftAsync(request.LayoutId, request.Revision, request.Plan, cancellationToken);
            return addOn.Envelope(new VenueLayoutSaveResponse(revision, []), StatusCodes.Status200OK);
        });
}

/// <summary><c>POST /spark/ticketing/venues/layouts/csv/import</c>: merge a CSV of seats into the draft (D32).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class ImportVenueLayoutCsv : IPostEndpoint<VenueLayoutCsvImportRequest>
{
    public static string Path => "/layouts/csv/import";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(VenueLayoutCsvImportRequest request, CancellationToken cancellationToken)
        => VenueResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            var revision = await venues.ImportCsvAsync(request?.LayoutId ?? "", request?.Revision ?? 0, request?.Csv ?? "", cancellationToken);
            return addOn.Envelope(new VenueLayoutSaveResponse(revision, []), StatusCodes.Status200OK);
        });
}

/// <summary><c>GET /spark/ticketing/venues/layouts/csv?layoutId=…&amp;version=…</c>: the seats as CSV (D32).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class ExportVenueLayoutCsv : IGetEndpoint
{
    public static string Path => "/layouts/csv";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => VenueResults.RunAsync(addOn, httpContext, async () =>
        {
            var query = httpContext.Request.Query;
            var version = query["version"].ToString() is { Length: > 0 } v ? v : "draft";
            var plan = await venues.GetPlanAsync(query["layoutId"].ToString(), version, httpContext.RequestAborted);
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(LayoutCsv.Export(plan.Plan))).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", LayoutCsv.Slug(plan.Name) + ".csv");
        });
}

/// <summary><c>POST /spark/ticketing/venues/layouts/publish</c>: publish the draft, or ask an approver (D8).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class PublishVenueLayout : IPostEndpoint<VenueLayoutPublishRequest>
{
    public static string Path => "/layouts/publish";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(VenueLayoutPublishRequest request, CancellationToken cancellationToken)
        => VenueResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            addOn.Envelope(await venues.PublishAsync(request?.LayoutId ?? "", request?.Revision ?? 0, cancellationToken), StatusCodes.Status200OK));
}

/// <summary><c>GET /spark/ticketing/venues/layouts/diff?layoutId=…</c>: the visual diff of the pending publication (owner, approvers).</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class DiffVenueLayout : IGetEndpoint
{
    public static string Path => "/layouts/diff";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => VenueResults.RunAsync(addOn, httpContext, async () =>
            addOn.Envelope(await venues.DiffAsync(httpContext.Request.Query["layoutId"].ToString(), httpContext.RequestAborted), StatusCodes.Status200OK));
}

/// <summary>
/// <c>POST /spark/ticketing/venues/layouts/background?layoutId=…</c>: upload a background image (the request body,
/// PNG, JPEG or WebP, at most <c>MaxBackgroundBytes</c>); answers its id. Owner only.
/// </summary>
[MemberOf<VenuesGroup>]
internal sealed partial class UploadVenueLayoutBackground : IPostEndpoint
{
    public static string Path => "/layouts/background";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkVenues venues;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => VenueResults.RunAsync(addOn, httpContext, async () =>
        {
            var request = httpContext.Request;
            var id = await venues.UploadBackgroundAsync(request.Query["layoutId"].ToString(), request.Body, request.ContentType ?? "", request.ContentLength ?? 0, httpContext.RequestAborted);
            return addOn.Envelope(new VenueLayoutRef(id, 0), StatusCodes.Status201Created);
        });
}

/// <summary>
/// <c>GET /spark/ticketing/venues/background?imageId=…</c>: a background image. Signed-in users only (an id is a
/// random GUID; the image is a venue drawing). Served with <c>nosniff</c> and the type the bytes were checked as.
/// </summary>
[MemberOf<VenuesGroup>]
internal sealed partial class GetVenueLayoutBackground : IGetEndpoint
{
    public static string Path => "/background";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => BackgroundResults.ServeAsync(store, addOn, httpContext, httpContext.Request.Query["imageId"].ToString());
}

internal static class BackgroundResults
{
    public static async Task<IResult> ServeAsync(IDocumentStore store, ISparkAddOnEndpoints addOn, HttpContext httpContext, string imageId)
    {
        if (!imageId.StartsWith("VenueLayoutBackgrounds/", StringComparison.Ordinal))
            return addOn.Refusal(httpContext);
        using var session = store.OpenAsyncSession();
        var image = await session.LoadAsync<VenueLayoutBackground>(imageId, httpContext.RequestAborted);
        if (image is null)
            return addOn.Refusal(httpContext);
        var attachment = await session.Advanced.Attachments.GetAsync(image.Id!, VenueLayoutBackground.AttachmentName, httpContext.RequestAborted);
        if (attachment is null)
            return addOn.Refusal(httpContext);
        var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer, httpContext.RequestAborted);
        attachment.Dispose();
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        // Immutable: a new upload is a new id.
        httpContext.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(buffer.ToArray(), image.ContentType);
    }
}

/// <summary><c>GET /spark/ticketing/venues/seatmap?snapshotId=…</c>: a snapshot's packed seat map (S7). Anonymous: buyers see it before signing in.</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class GetSeatMap : IGetEndpoint
{
    public static string Path => "/seatmap";

    [Inject] private readonly VenueSeatMaps maps;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var payload = await maps.GetPayloadAsync(httpContext.Request.Query["snapshotId"].ToString(), httpContext.RequestAborted);
        if (payload is null)
            return addOn.Refusal(httpContext);
        // A snapshot never changes.
        httpContext.Response.Headers.CacheControl = "public, max-age=3600";
        return addOn.Envelope(payload, StatusCodes.Status200OK);
    }
}

/// <summary><c>GET /spark/ticketing/venues/seatmap/state?snapshotId=…</c>: the seat states now (free, held, sold) and what is left per block.</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class GetSeatMapState : IGetEndpoint
{
    public static string Path => "/seatmap/state";

    [Inject] private readonly VenueSeatMaps maps;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var frame = await maps.GetStateAsync(httpContext.Request.Query["snapshotId"].ToString(), fresh: false, httpContext.RequestAborted);
        if (frame is null)
            return addOn.Refusal(httpContext);
        httpContext.Response.Headers.CacheControl = "no-store";
        return addOn.Envelope(frame, StatusCodes.Status200OK);
    }
}

/// <summary><c>GET /spark/ticketing/venues/seatmap/background?snapshotId=…</c>: the snapshot's background image, anonymous like the map.</summary>
[MemberOf<VenuesGroup>]
internal sealed partial class GetSeatMapBackground : IGetEndpoint
{
    public static string Path => "/seatmap/background";

    [Inject] private readonly VenueSeatMaps maps;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var snapshot = await maps.GetSnapshotAsync(httpContext.Request.Query["snapshotId"].ToString(), httpContext.RequestAborted);
        return snapshot?.Plan.Background is { ImageId: { Length: > 0 } imageId }
            ? await BackgroundResults.ServeAsync(store, addOn, httpContext, imageId)
            : addOn.Refusal(httpContext);
    }
}
