using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Ticketing.Events.Answers;
using MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Discovery;
using MintPlayer.Spark.Commerce.Ticketing.Events.PreSelect;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Endpoints;

internal class TicketingEventsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/ticketing/events";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>
/// Maps refusals: a checkout or ticketing code as 409 with <see cref="SparkTicketingRefusal"/>; refused attendee answers
/// as 409 with their per-question errors; a validation error as 400; "not yours" and "not found" as the missing-row
/// refusal (401 anonymous, 404 otherwise, #453).
/// </summary>
internal static class TicketingResults
{
    public static async Task<IResult> RunAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<object?>> action)
    {
        try
        {
            var result = await action();
            return result is null ? addOn.Refusal(http) : addOn.Envelope(result, StatusCodes.Status200OK);
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(http);
        }
        catch (TicketingException ex) when (ex.Code is TicketingRefusals.Forbidden or TicketingRefusals.EventNotFound)
        {
            return addOn.Refusal(http);
        }
        catch (TicketingException ex)
        {
            return addOn.Envelope(new SparkTicketingRefusal(ex.Code, ex.Subject, []), StatusCodes.Status409Conflict);
        }
        catch (CheckoutException ex) when (ex.Code is CheckoutRefusals.Forbidden or CheckoutRefusals.OrderNotFound)
        {
            return addOn.Refusal(http);
        }
        catch (CheckoutException ex)
        {
            return addOn.Envelope(new SparkTicketingRefusal(ex.Code, ex.Subject, []), StatusCodes.Status409Conflict);
        }
        catch (AttendeeAnswerException ex)
        {
            var closed = ex.Errors.Any(e => e.Code == "closed");
            if (ex.Errors.Any(e => e.Code == "notFound"))
                return addOn.Refusal(http);
            return addOn.Envelope(new SparkTicketingRefusal(closed ? TicketingRefusals.AnswersClosed : TicketingRefusals.AnswersRefused, null,
                ex.Errors.Select(e => new SparkTicketingFieldError(e.QuestionKey, e.Code)).ToList()), StatusCodes.Status409Conflict);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
    }

    public static string Query(HttpContext http, string name) => http.Request.Query[name].ToString();
}

/// <summary><c>GET /spark/ticketing/events/public?slug=</c>: a published event's page data. Anonymous.</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class GetPublicEvent : IGetEndpoint
{
    public static string Path => "/public";

    [Inject] private readonly ISparkTicketEvents events;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => TicketingResults.RunAsync(addOn, httpContext, async () => await events.GetPublicAsync(TicketingResults.Query(httpContext, "slug"), httpContext.RequestAborted));
}

/// <summary><c>GET /spark/ticketing/events/availability?eventId=&amp;phaseId=</c>: places left per date (the waiting page's too). Anonymous.</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class GetEventAvailability : IGetEndpoint
{
    public static string Path => "/availability";

    [Inject] private readonly ISparkTicketEvents events;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var phaseId = TicketingResults.Query(httpContext, "phaseId");
        return TicketingResults.RunAsync(addOn, httpContext, async () => await events.GetAvailabilityAsync(
            TicketingResults.Query(httpContext, "eventId"), string.IsNullOrEmpty(phaseId) ? null : phaseId, httpContext.RequestAborted));
    }
}

/// <summary><c>GET /spark/ticketing/events/calendar?dateId=&amp;culture=</c>: the date as an iCalendar file (GB9). Anonymous.</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class GetEventCalendar : IGetEndpoint
{
    public static string Path => "/calendar";

    [Inject] private readonly ISparkTicketEvents events;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var culture = TicketingResults.Query(httpContext, "culture");
        var ics = await events.GetCalendarAsync(TicketingResults.Query(httpContext, "dateId"), string.IsNullOrEmpty(culture) ? null : culture, httpContext.RequestAborted);
        return ics is null
            ? addOn.Refusal(httpContext)
            : Results.File(System.Text.Encoding.UTF8.GetBytes(ics), EventCalendar.ContentType, "event.ics");
    }
}

/// <summary><c>POST /spark/ticketing/events/quick</c>: a one-off event on one screen (§4.11). Members of the organization.</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class QuickCreateEvent : IPostEndpoint<SparkQuickEventRequest>
{
    public static string Path => "/quick";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkTicketEvents events;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkQuickEventRequest request, CancellationToken cancellationToken)
        => TicketingResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await events.QuickCreateAsync(request, cancellationToken));
}

/// <summary><c>GET /spark/ticketing/events/attendees?number=</c>: the attendee questions of the caller's own order (GB3).</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class GetAttendeeQuestions : IGetEndpoint
{
    public static string Path => "/attendees";

    [Inject] private readonly IAttendeeAnswers answers;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => TicketingResults.RunAsync(addOn, httpContext, async () => await answers.GetAsync(TicketingResults.Query(httpContext, "number"), httpContext.RequestAborted));
}

/// <summary><c>POST /spark/ticketing/events/attendees</c>: the answers of one ticket position of the caller's own order (GB3).</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class SaveAttendeeAnswers : IPostEndpoint<SparkAttendeeAnswersRequest>
{
    public static string Path => "/attendees";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IAttendeeAnswers answers;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkAttendeeAnswersRequest request, CancellationToken cancellationToken)
        => TicketingResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await answers.SaveAsync(request, cancellationToken));
}

/// <summary><c>GET /spark/ticketing/events/preselect?phaseId=</c>: the caller's pre-selection (D25).</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class GetPreSelection : IGetEndpoint
{
    public static string Path => "/preselect";

    [Inject] private readonly IPreSelectOrders preSelect;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var selection = await preSelect.GetAsync(TicketingResults.Query(httpContext, "phaseId"), httpContext.RequestAborted);
        // No pre-selection yet is an empty one, not a refusal.
        return addOn.Envelope(selection ?? new SparkPreSelection(TicketingResults.Query(httpContext, "phaseId"), [], [], null), StatusCodes.Status200OK);
    }
}

/// <summary><c>POST /spark/ticketing/events/preselect</c>: saves the caller's pre-selection (D25). A guest gets the visitor cookie here (X1).</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class SavePreSelection : IPostEndpoint<SparkPreSelectionRequest>
{
    public static string Path => "/preselect";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IPreSelectOrders preSelect;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPreSelectionRequest request, CancellationToken cancellationToken)
        => TicketingResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await preSelect.SaveAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/events/preselect/apply</c>: at admission, holds the pre-selection with the admission token (D25).</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class ApplyPreSelection : IPostEndpoint<SparkPreSelectApplyRequest>
{
    public static string Path => "/preselect/apply";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IPreSelectOrders preSelect;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPreSelectApplyRequest request, CancellationToken cancellationToken)
        => TicketingResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await preSelect.ApplyAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/ticketing/events/preselect/release</c>: one click gives the held pre-selection back (D25).</summary>
[MemberOf<TicketingEventsGroup>]
internal sealed partial class ReleasePreSelection : IPostEndpoint<SparkPhaseRequest>
{
    public static string Path => "/preselect/release";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IPreSelectOrders preSelect;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPhaseRequest request, CancellationToken cancellationToken)
        => TicketingResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await preSelect.ReleaseAsync(request.PhaseId, cancellationToken));
}
