using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Printing.Services;

namespace MintPlayer.Spark.Printing.Endpoints;

// The agent's endpoints (PRD §4.11; Q-W14): the agent connects OUT to the app, so it needs no inbound firewall rule, and
// authenticates with a Spark personal access token of its own principal. It sees only the jobs of its own printers.

internal class PrintAgentGroup : IEndpointGroup
{
    public static string Prefix => "/spark/printing/agent";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
    {
        var scheme = services.GetRequiredService<IOptions<SparkPrintingOptions>>().Value.AgentAuthenticationScheme;
        // Bearer only: no cookie, so no antiforgery token either.
        group.RequireAuthorization(new AuthorizationPolicyBuilder(scheme).RequireAuthenticatedUser().Build());
        group.DisableAntiforgery();
    }
}

public sealed record PrintPullRequest(int? Max);

/// <summary>A job as the agent receives it: the bytes to send to <see cref="Address"/>.</summary>
public sealed record PrintAgentJob(string Id, string Printer, string? Address, string ContentType, int? Dpi, string ContentBase64, string Title);

public sealed record PrintAckRequest(string? JobId, bool Printed, string? Reason);

public sealed record PrintAckResponse(string Outcome);

public sealed record PrintScaleRequest(string? Station, decimal Grams, bool Stable);

internal static class AgentCaller
{
    /// <summary>The configured agent the caller's token belongs to, or null (the endpoint then answers 403).</summary>
    public static string? Resolve(HttpContext http, IPrintAgents agents)
        => agents.AgentOf(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub"));
}

/// <summary><c>POST /spark/printing/agent/pull</c>: the agent's next jobs, marked delivered.</summary>
[MemberOf<PrintAgentGroup>]
internal sealed partial class PullPrintJobs : IPostEndpoint<PrintPullRequest>
{
    public static string Path => "/pull";

    [Inject] private readonly IPrintJobs jobs;
    [Inject] private readonly IPrintAgents agents;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PrintPullRequest request, CancellationToken cancellationToken)
    {
        if (AgentCaller.Resolve(httpContextAccessor.HttpContext!, agents) is not { } agent)
            return Results.Forbid();
        var delivered = await jobs.PullAsync(agent, request?.Max ?? 20, cancellationToken);
        return Results.Ok(delivered.Select(d => new PrintAgentJob(d.JobId, d.Printer, d.Address, d.ContentType, d.Dpi, Convert.ToBase64String(d.Content), d.Title)).ToList());
    }
}

/// <summary><c>POST /spark/printing/agent/ack</c>: printed or failed, recorded once.</summary>
[MemberOf<PrintAgentGroup>]
internal sealed partial class AcknowledgePrintJob : IPostEndpoint<PrintAckRequest>
{
    public static string Path => "/ack";

    [Inject] private readonly IPrintJobs jobs;
    [Inject] private readonly IPrintAgents agents;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PrintAckRequest request, CancellationToken cancellationToken)
    {
        if (AgentCaller.Resolve(httpContextAccessor.HttpContext!, agents) is not { } agent)
            return Results.Forbid();
        if (request?.JobId is not { Length: > 0 } jobId)
            return Results.BadRequest();
        var outcome = await jobs.AcknowledgeAsync(agent, jobId, request.Printed, request.Reason, cancellationToken);
        return outcome == PrintAckOutcome.NotFound ? Results.NotFound() : Results.Ok(new PrintAckResponse(outcome.ToString()));
    }
}

/// <summary><c>POST /spark/printing/agent/heartbeat</c>: version, reachability, uncertain jobs; answers the update check and the agent's printers.</summary>
[MemberOf<PrintAgentGroup>]
internal sealed partial class PrintAgentHeartbeat : IPostEndpoint<PrintHeartbeatRequest>
{
    public static string Path => "/heartbeat";

    [Inject] private readonly IPrintAgents agents;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PrintHeartbeatRequest request, CancellationToken cancellationToken)
    {
        if (AgentCaller.Resolve(httpContextAccessor.HttpContext!, agents) is not { } agent)
            return Results.Forbid();
        return Results.Ok(await agents.HeartbeatAsync(agent, request ?? new PrintHeartbeatRequest(null, null, null, null), cancellationToken));
    }
}

/// <summary><c>POST /spark/printing/agent/scale</c>: a scale reading at one of the agent's stations (packing, weigh step).</summary>
[MemberOf<PrintAgentGroup>]
internal sealed partial class PrintAgentScale : IPostEndpoint<PrintScaleRequest>
{
    public static string Path => "/scale";

    [Inject] private readonly IPrintAgents agents;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PrintScaleRequest request, CancellationToken cancellationToken)
    {
        if (AgentCaller.Resolve(httpContextAccessor.HttpContext!, agents) is not { } agent)
            return Results.Forbid();
        if (request?.Station is not { Length: > 0 } station)
            return Results.BadRequest();
        return await agents.RecordScaleAsync(agent, station, request.Grams, request.Stable, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }
}

// The station's endpoints (the PDF dialog and the bridges, ng-spark/printing, WM9): a signed-in operator's browser takes the
// jobs of its station's dialog and bridge printers, and the PDF fallback of agent jobs that did not print in time.

internal class PrintStationGroup : IEndpointGroup
{
    public static string Prefix => "/spark/printing/station";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.RequireAuthorization();
}

public sealed record PrintTakeRequest(string? Station, string? JobId);

/// <summary>The content a station prints: PDF bytes for the dialog, ZPL text for a bridge.</summary>
[SparkContract]
public sealed record StationPrintContent(string Id, string Title, string ContentType, int? Dpi, string? DeviceName, string ContentBase64);

public sealed record PrintStationAckRequest(string? Station, string? JobId, bool Printed, string? Reason);

internal static class StationAccess
{
    /// <summary>Station work needs the right to read print jobs (the operators' slot).</summary>
    public static Task<bool> AllowedAsync(IPermissionService permissions, CancellationToken cancellationToken)
        => permissions.IsAllowedAsync("Read", "PrintJob", cancellationToken);
}

/// <summary><c>GET /spark/printing/station/queue?station=…</c>: the station's jobs and fallbacks.</summary>
[MemberOf<PrintStationGroup>]
internal sealed partial class StationPrintQueue : IGetEndpoint
{
    public static string Path => "/queue";

    [Inject] private readonly IPrintJobs jobs;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var station = httpContext.Request.Query["station"].ToString();
        if (station.Length == 0 || !await StationAccess.AllowedAsync(permissions, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        return Results.Ok(await jobs.StationQueueAsync(station, httpContext.RequestAborted));
    }
}

/// <summary><c>POST /spark/printing/station/take</c>: the content of one job, marked delivered (a fallback is withdrawn from the agent).</summary>
[MemberOf<PrintStationGroup>]
internal sealed partial class TakeStationPrintJob : IPostEndpoint<PrintTakeRequest>
{
    public static string Path => "/take";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IPrintJobs jobs;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PrintTakeRequest request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.Station is not { Length: > 0 } station || request.JobId is not { Length: > 0 } jobId
            || !await StationAccess.AllowedAsync(permissions, cancellationToken))
            return addOn.Refusal(http);
        if (await jobs.TakeAtStationAsync(station, jobId, cancellationToken) is not { } delivery)
            return addOn.Refusal(http);
        return Results.Ok(new StationPrintContent(delivery.JobId, delivery.Title, delivery.ContentType, delivery.Dpi, delivery.DeviceName,
            Convert.ToBase64String(delivery.Content)));
    }
}

/// <summary><c>POST /spark/printing/station/ack</c>: the station printed (or could not print) a job it took.</summary>
[MemberOf<PrintStationGroup>]
internal sealed partial class AcknowledgeStationPrintJob : IPostEndpoint<PrintStationAckRequest>
{
    public static string Path => "/ack";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IPrintJobs jobs;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PrintStationAckRequest request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.Station is not { Length: > 0 } station || request.JobId is not { Length: > 0 } jobId
            || !await StationAccess.AllowedAsync(permissions, cancellationToken))
            return addOn.Refusal(http);
        var outcome = await jobs.AcknowledgeAtStationAsync(station, jobId, request.Printed, request.Reason, cancellationToken);
        return outcome == PrintAckOutcome.NotFound ? addOn.Refusal(http) : Results.Ok(new PrintAckResponse(outcome.ToString()));
    }
}
