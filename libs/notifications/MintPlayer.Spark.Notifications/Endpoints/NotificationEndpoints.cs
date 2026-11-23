using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Notifications.Actions;
using MintPlayer.Spark.Notifications.Services;
using MintPlayer.Spark.WebPush;

namespace MintPlayer.Spark.Notifications.Endpoints;

/// <summary>
/// <c>/spark/notifications</c>. Not <c>RequireAuthorization()</c> as a group (<c>docs/notification_bell_PRD.md</c> R8):
/// mark-read serves visitors too and checks the caller itself; the push endpoints require a user one by one.
/// </summary>
internal class NotificationsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/notifications";
}

internal static class NotificationCaller
{
    public static string? Id(IHttpContextAccessor accessor)
        => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;
}

public sealed record MarkNotificationsReadBody(string[]? Ids);

public sealed record MarkNotificationsReadResponse(int Marked);

/// <summary>
/// <c>POST /spark/notifications/read</c>: the given ids, or every unread notification of the caller, a user or a
/// visitor. 401 for a caller with neither. The visitor cookie is an ambient credential, so the antiforgery token is
/// required for visitors too (PRD S5).
/// </summary>
[MemberOf<NotificationsGroup>]
internal sealed partial class MarkNotificationsRead : IPostEndpoint<MarkNotificationsReadBody>
{
    public static string Path => "/read";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly NotificationInbox inbox;
    [Inject] private readonly ISparkCurrentPrincipal principal;

    public override async Task<IResult> HandleAsync(MarkNotificationsReadBody request, CancellationToken cancellationToken)
    {
        var owner = NotificationOwner.Of(principal);
        if (!owner.IsKnown)
            return TypedResults.Unauthorized();
        var marked = await inbox.MarkReadAsync(owner, request?.Ids, cancellationToken);
        return TypedResults.Ok(new MarkNotificationsReadResponse(marked));
    }
}

public sealed record WebPushKeyResponse(string PublicKey);

/// <summary><c>GET /spark/notifications/push/key</c>: the VAPID public key the browser subscribes with; 404 while Web Push is not configured.</summary>
[MemberOf<NotificationsGroup>]
internal sealed partial class GetWebPushKey : IGetEndpoint
{
    public static string Path => "/push/key";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly IOptions<SparkNotificationsOptions> options;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var push = options.Value.WebPush;
        return Task.FromResult<IResult>(push.IsConfigured ? TypedResults.Ok(new WebPushKeyResponse(push.PublicKey!.Trim())) : TypedResults.NotFound());
    }
}

public sealed record PushSubscriptionKeys(string? P256dh, string? Auth);

/// <summary>The body of <c>PushSubscription.toJSON()</c>.</summary>
public sealed record PushSubscriptionBody(string? Endpoint, PushSubscriptionKeys? Keys);

/// <summary><c>POST /spark/notifications/push/subscribe</c>: stores the caller's device. 400 for a malformed or disallowed subscription.</summary>
[MemberOf<NotificationsGroup>]
internal sealed partial class SubscribeWebPush : IPostEndpoint<PushSubscriptionBody>
{
    public static string Path => "/push/subscribe";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly NotificationInbox inbox;
    [Inject] private readonly IOptions<SparkNotificationsOptions> options;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(PushSubscriptionBody request, CancellationToken cancellationToken)
    {
        if (NotificationCaller.Id(httpContextAccessor) is not { } userId)
            return TypedResults.Unauthorized();
        if (!options.Value.WebPush.IsConfigured)
            return TypedResults.NotFound();
        try
        {
            var device = httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();
            await inbox.SubscribeAsync(userId, new WebPushSubscription(request?.Endpoint ?? "", request?.Keys?.P256dh ?? "", request?.Keys?.Auth ?? ""), device, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record UnsubscribeWebPushBody(string? Endpoint);

/// <summary><c>POST /spark/notifications/push/unsubscribe</c>: forgets the caller's device with that endpoint.</summary>
[MemberOf<NotificationsGroup>]
internal sealed partial class UnsubscribeWebPush : IPostEndpoint<UnsubscribeWebPushBody>
{
    public static string Path => "/push/unsubscribe";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly NotificationInbox inbox;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(UnsubscribeWebPushBody request, CancellationToken cancellationToken)
    {
        if (NotificationCaller.Id(httpContextAccessor) is not { } userId)
            return TypedResults.Unauthorized();
        await inbox.UnsubscribeAsync(userId, request?.Endpoint ?? "", cancellationToken);
        return TypedResults.NoContent();
    }
}
