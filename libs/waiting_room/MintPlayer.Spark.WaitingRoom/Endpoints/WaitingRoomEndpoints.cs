using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Challenges.Deceptive;
using MintPlayer.Spark.Challenges.Risk;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.WaitingRoom.Contracts;

namespace MintPlayer.Spark.WaitingRoom.Endpoints;

/// <summary>
/// Public: guests queue too. The writes are antiforgery-protected like every anonymous mutating call that carries a
/// visitor cookie (X1); the join issues the cookie itself (a strictly necessary one, by an action).
/// </summary>
internal class WaitingRoomGroup : IEndpointGroup
{
    public static string Prefix => "/spark/waiting-room";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.AllowAnonymous();
}

internal static class WaitingRoomAnswers
{
    public static string? Key(HttpContext http) => http.Request.RouteValues["key"]?.ToString()?.ToLowerInvariant();

    public static IResult NoStore(ISparkAddOnEndpoints addOn, HttpContext http, object? result, int status = StatusCodes.Status200OK)
    {
        http.Response.Headers.CacheControl = "no-store";
        return addOn.Envelope(result, status);
    }
}

/// <summary><c>GET /spark/waiting-room/{key}</c>: the room (<see cref="WaitingRoomInfo"/>).</summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class GetWaitingRoom : IGetEndpoint
{
    public static string Path => "/{key}";

    [Inject] private readonly ISparkWaitingRooms rooms;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => WaitingRoomAnswers.Key(httpContext) is { } key && await rooms.InfoAsync(key, httpContext.RequestAborted) is { } info
            ? WaitingRoomAnswers.NoStore(addOn, httpContext, info)
            : addOn.Refusal(httpContext);
}

/// <summary><c>GET /spark/waiting-room/{key}/entry</c>: the caller's entry (<see cref="WaitingRoomEntryView"/>), or 404.</summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class GetWaitingRoomEntry : IGetEndpoint
{
    public static string Path => "/{key}/entry";

    [Inject] private readonly ISparkWaitingRooms rooms;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => WaitingRoomAnswers.Key(httpContext) is { } key && await rooms.GetEntryAsync(key, httpContext.RequestAborted) is { } entry
            ? WaitingRoomAnswers.NoStore(addOn, httpContext, entry)
            : addOn.Refusal(httpContext);
}

/// <summary><c>GET /spark/waiting-room/{key}/draw</c>: the proof of the shuffle or draw (<see cref="WaitingRoomDrawProof"/>).</summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class GetWaitingRoomDraw : IGetEndpoint
{
    public static string Path => "/{key}/draw";

    [Inject] private readonly ISparkWaitingRooms rooms;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => WaitingRoomAnswers.Key(httpContext) is { } key && await rooms.DrawProofAsync(key, httpContext.RequestAborted) is { } proof
            ? WaitingRoomAnswers.NoStore(addOn, httpContext, proof)
            : addOn.Refusal(httpContext);
}

/// <summary>
/// <c>POST /spark/waiting-room/{key}/join</c>: joins (<see cref="WaitingRoomJoinResult"/>). <c>joined</c> is 200; a challenge
/// to solve is 428; sign-in, phone or account age is 403; not open, closed or sold out is 409; slow down is 429 with
/// <c>Retry-After</c>; a refusal on deterministic evidence is 403 with the decision id to appeal.
/// </summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class JoinWaitingRoom : IPostEndpoint<WaitingRoomJoinBody>
{
    public static string Path => "/{key}/join";

    [RouteParam] public string Key { get; set; } = "";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkWaitingRooms rooms;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WaitingRoomJoinBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (WaitingRoomAnswers.Key(http) is not { } key || await rooms.JoinAsync(key, request ?? new WaitingRoomJoinBody(), cancellationToken) is not { } result)
            return addOn.Refusal(http);
        var status = result.Outcome switch
        {
            "joined" => StatusCodes.Status200OK,
            "challenge" => StatusCodes.Status428PreconditionRequired,
            "slow-down" => StatusCodes.Status429TooManyRequests,
            "sign-in-required" or "phone-verification-required" or "account-too-new" or "refused" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status409Conflict,
        };
        if (result.RetryAfterSeconds is { } seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return WaitingRoomAnswers.NoStore(addOn, http, result, status);
    }
}

/// <summary>
/// <c>POST /spark/waiting-room/{key}/presence</c>: a sign of life. Answers a presence check (the page sends it by itself
/// while the tab is visible), brings a missed entry back at its rank, records whether the tab is hidden.
/// </summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class WaitingRoomPresence : IPostEndpoint<WaitingRoomPresenceBody>
{
    public static string Path => "/{key}/presence";

    [RouteParam] public string Key { get; set; } = "";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkWaitingRooms rooms;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WaitingRoomPresenceBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        return WaitingRoomAnswers.Key(http) is { } key && await rooms.PresenceAsync(key, request?.Visible ?? true, cancellationToken) is { } entry
            ? WaitingRoomAnswers.NoStore(addOn, http, entry)
            : addOn.Refusal(http);
    }
}

public sealed record WaitingRoomLeaveBody;

/// <summary><c>POST /spark/waiting-room/{key}/leave</c>: leaves the queue.</summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class LeaveWaitingRoom : IPostEndpoint<WaitingRoomLeaveBody>
{
    public static string Path => "/{key}/leave";

    [RouteParam] public string Key { get; set; } = "";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkWaitingRooms rooms;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WaitingRoomLeaveBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        return WaitingRoomAnswers.Key(http) is { } key && await rooms.LeaveAsync(key, cancellationToken)
            ? WaitingRoomAnswers.NoStore(addOn, http, new { left = true })
            : addOn.Refusal(http);
    }
}

/// <summary>
/// <c>GET /spark/waiting-room/{key}/priority-access</c>: a decoy (deceptive measure <c>Decoys</c>, off by default). No page
/// links to it visibly; a hit is deterministic evidence against the caller and answers a plausible fake. With the measure
/// off it is a plain 404.
/// </summary>
[MemberOf<WaitingRoomGroup>]
internal sealed partial class WaitingRoomDecoy : IGetEndpoint
{
    public static string Path => "/{key}/priority-access";

    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        if (services.GetService<ISparkRiskPipeline>() is not { } pipeline || services.GetService<ISparkDeceptiveMeasures>() is not { } deceptive)
            return addOn.Refusal(httpContext);
        var evidence = new SparkRiskEvidence("decoy", 1, Deterministic: true, Detail: "requested a decoy route", DeceptiveTrigger: true);
        var context = pipeline.ContextFor(httpContext, "decoy", WaitingRoomAnswers.Key(httpContext));
        if (!await deceptive.ApplyAsync(SparkDeceptiveMeasureNames.Decoys, context, new SparkRiskDecision(SparkRiskAction.Block, 1, [evidence]), httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        await pipeline.RecordAsync(context, evidence, httpContext.RequestAborted);
        return WaitingRoomAnswers.NoStore(addOn, httpContext, new { queued = true, priority = true });
    }
}
