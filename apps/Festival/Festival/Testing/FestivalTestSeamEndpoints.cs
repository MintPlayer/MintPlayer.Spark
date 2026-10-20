using Festival.Seed;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Payments.Test;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;

namespace Festival.Testing;

/// <summary>
/// <c>/festival-test</c>: the test seams (<see cref="FestivalTestSeams"/>). The whole group exists only when
/// <c>Festival:TestSeams:Enabled</c> is true, and every endpoint in it needs the antiforgery token.
/// </summary>
public sealed class FestivalTestSeamsGroup : IEndpointGroup
{
    public static string Prefix => "/festival-test";

    static bool IEndpointGroup.IsEnabled(IServiceProvider services)
        => FestivalTestSeams.IsEnabled(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>());

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    /// <summary>Only a platform administrator gets an answer other than 404 from a seam.</summary>
    internal static Task<bool> IsPlatformAdminAsync(IPermissionService permissions, CancellationToken cancellationToken = default)
        => permissions.IsAllowedAsync("ManageSellers", "Commerce", cancellationToken);
}

/// <summary><c>POST /festival-test/organizer</c>: makes the calling administrator an organiser of the demo organisation (Owner, every seller role).</summary>
[MemberOf<FestivalTestSeamsGroup>]
internal sealed partial class OrganizerSeam : IPostEndpoint
{
    public static string Path => "/organizer";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IDocumentStore store;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!await FestivalTestSeamsGroup.IsPlatformAdminAsync(permissions, httpContext.RequestAborted) || currentUser.Id is not { } userId)
            return Results.NotFound();
        await FestivalOrganizers.GrantAsync(store, userId, httpContext.User.Identity?.Name ?? userId, httpContext.RequestAborted);
        return Results.NoContent();
    }
}

/// <summary>A test payment (the <c>?id=</c> of the test pay page).</summary>
public sealed record FestivalPaymentRequest(string ProviderPaymentId);

/// <summary><c>POST /festival-test/payments/dispute</c>: the buyer's bank opens a dispute on the payment (GB1). Answers the dispute id.</summary>
[MemberOf<FestivalTestSeamsGroup>]
internal sealed partial class OpenDisputeSeam : IPostEndpoint<FestivalPaymentRequest>
{
    public static string Path => "/payments/dispute";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly TestPaymentSimulator simulator;

    public override async Task<IResult> HandleAsync(FestivalPaymentRequest request, CancellationToken cancellationToken)
        => await FestivalTestSeamsGroup.IsPlatformAdminAsync(permissions, cancellationToken)
            ? Results.Json(new { disputeId = await simulator.OpenDisputeAsync(request.ProviderPaymentId, cancellationToken: cancellationToken) })
            : Results.NotFound();
}

/// <summary>The body of <see cref="CloseDisputeSeam"/>.</summary>
public sealed record FestivalCloseDisputeRequest(string ProviderPaymentId, string DisputeId, bool Won);

/// <summary><c>POST /festival-test/payments/dispute/close</c>: the dispute is decided, won or lost.</summary>
[MemberOf<FestivalTestSeamsGroup>]
internal sealed partial class CloseDisputeSeam : IPostEndpoint<FestivalCloseDisputeRequest>
{
    public static string Path => "/payments/dispute/close";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly TestPaymentSimulator simulator;

    public override async Task<IResult> HandleAsync(FestivalCloseDisputeRequest request, CancellationToken cancellationToken)
    {
        if (!await FestivalTestSeamsGroup.IsPlatformAdminAsync(permissions, cancellationToken))
            return Results.NotFound();
        await simulator.CloseDisputeAsync(request.ProviderPaymentId, request.DisputeId, request.Won, cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>The body of <see cref="RefundBehaviourSeam"/>: <c>Succeed</c>, <c>Pending</c>, <c>InsufficientBalance</c> or <c>Fail</c>.</summary>
public sealed record FestivalRefundBehaviourRequest(string ProviderPaymentId, TestRefundBehaviour Behaviour);

/// <summary><c>POST /festival-test/payments/refund-behaviour</c>: how the provider answers the next refund of the payment (the mass refund's mid-way failure).</summary>
[MemberOf<FestivalTestSeamsGroup>]
internal sealed partial class RefundBehaviourSeam : IPostEndpoint<FestivalRefundBehaviourRequest>
{
    public static string Path => "/payments/refund-behaviour";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly TestPaymentSimulator simulator;

    public override async Task<IResult> HandleAsync(FestivalRefundBehaviourRequest request, CancellationToken cancellationToken)
    {
        if (!await FestivalTestSeamsGroup.IsPlatformAdminAsync(permissions, cancellationToken))
            return Results.NotFound();
        simulator.SetRefundBehaviour(request.ProviderPaymentId, request.Behaviour);
        return Results.NoContent();
    }
}

/// <summary><c>POST /festival-test/payments/deliver-late</c>: releases the webhooks held back by "Pay, webhook late". Answers how many.</summary>
[MemberOf<FestivalTestSeamsGroup>]
internal sealed partial class DeliverLateSeam : IPostEndpoint
{
    public static string Path => "/payments/deliver-late";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly TestPaymentSimulator simulator;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => await FestivalTestSeamsGroup.IsPlatformAdminAsync(permissions, httpContext.RequestAborted)
            ? Results.Json(new { delivered = await simulator.DeliverLateAsync(httpContext.RequestAborted) })
            : Results.NotFound();
}

/// <summary>The body of <see cref="QueuedEventSeam"/>: offsets in seconds from now.</summary>
public sealed record FestivalQueuedEventRequest(WaitingRoomMode Mode, int OpensInSeconds, int StartsInSeconds, bool PreSelect = false, int Capacity = 100, int PerBuyerLimit = 4);

/// <summary><c>POST /festival-test/events/queued</c>: a new queued sale on a schedule the test chooses (ballot, pre-queue, PreSelect).</summary>
[MemberOf<FestivalTestSeamsGroup>]
internal sealed partial class QueuedEventSeam : IPostEndpoint<FestivalQueuedEventRequest>
{
    public static string Path => "/events/queued";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly FestivalTestEvents events;

    public override async Task<IResult> HandleAsync(FestivalQueuedEventRequest request, CancellationToken cancellationToken)
        => await FestivalTestSeamsGroup.IsPlatformAdminAsync(permissions, cancellationToken)
            ? Results.Json(await events.CreateQueuedAsync(
                request.Mode,
                TimeSpan.FromSeconds(request.OpensInSeconds),
                TimeSpan.FromSeconds(request.StartsInSeconds),
                request.PreSelect,
                request.Capacity,
                request.PerBuyerLimit,
                cancellationToken))
            : Results.NotFound();
}
