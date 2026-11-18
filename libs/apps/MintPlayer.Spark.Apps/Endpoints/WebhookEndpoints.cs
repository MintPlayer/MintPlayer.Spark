using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Services;

namespace MintPlayer.Spark.Apps.Endpoints;

internal class AppsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/apps";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.RequireAuthorization();
}

internal static class AppsHttp
{
    public static string? UserId(HttpContext http)
        => http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;

    public static IResult ToResult<T>(WebhookResult<T> result, Func<T, IResult>? ok = null) => result.Outcome switch
    {
        WebhookOutcome.Ok => ok is null ? TypedResults.Ok(result.Value) : ok(result.Value!),
        WebhookOutcome.Invalid => TypedResults.BadRequest(new { error = result.Error }),
        WebhookOutcome.Conflict => TypedResults.Conflict(new { error = result.Error }),
        _ => TypedResults.NotFound(),
    };

    public static void RequireAntiforgery(RouteHandlerBuilder builder) => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
}

public sealed record WebhookIdBody(string? Id);

/// <summary>
/// <c>GET /spark/apps/webhooks/catalog</c>: every event as an OpenAPI 3.1 document (D19), anonymous so codegen
/// (<c>spark webhooks generate</c>) can read it. <c>x-spark-signing-keys</c> lists the site's <c>v2a</c> public keys.
/// </summary>
[MemberOf<AppsGroup>]
internal sealed partial class GetWebhookCatalog : IGetEndpoint
{
    public static string Path => "/webhooks/catalog";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();

    [Inject] private readonly WebhookEventCatalog catalog;
    [Inject] private readonly WebhookSigners signers;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly IServiceProvider services;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var document = catalog.ToOpenApi(options.Value.CatalogTitle, options.Value.CatalogVersion);
        // The permissions an App may ask for that mean something here: the events' and those mapped to groups (D17).
        var permissions = catalog.Events.Select(e => e.Permission).OfType<string>()
            .Concat(services.GetService<AppPermissionGroups>()?.Permissions ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        document["x-spark-permissions"] = new JsonArray([.. permissions.Select(p => JsonValue.Create(p))]);
        if (signers.PublicKeys.Count > 0)
        {
            var keys = new JsonObject();
            foreach (var (version, key) in signers.PublicKeys)
                keys[version] = key;
            document["x-spark-signing-keys"] = keys;
        }
        return Task.FromResult<IResult>(TypedResults.Text(document.ToJsonString(), "application/json"));
    }
}

/// <summary><c>GET /spark/apps/webhooks/owners</c>: the accounts and organizations the caller manages webhooks for.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class ListWebhookOwners : IGetEndpoint
{
    public static string Path => "/webhooks/owners";

    [Inject] private readonly WebhookEndpointManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? TypedResults.Ok(await manager.ListOwnersAsync(userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>GET /spark/apps/webhooks/endpoints?ownerKind=…&amp;ownerKey=…</c>: the owner's webhooks (never their secrets).</summary>
[MemberOf<AppsGroup>]
internal sealed partial class ListWebhookEndpoints : IGetEndpoint
{
    public static string Path => "/webhooks/endpoints";

    [QueryParam("ownerKind")] public string? OwnerKind { get; set; }
    [QueryParam("ownerKey")] public string? OwnerKey { get; set; }

    [Inject] private readonly WebhookEndpointManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? AppsHttp.ToResult(await manager.ListAsync(OwnerKind, OwnerKey, userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/endpoints</c>: registers a webhook; the answer holds its secret, shown this once.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class CreateWebhookEndpoint : IPostEndpoint<WebhookEndpointInput>
{
    public static string Path => "/webhooks/endpoints";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookEndpointInput request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.CreateAsync(request ?? new(null, null, null, null, null), userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/endpoints/update</c>: URL, description and events; null leaves a field as it is.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class UpdateWebhookEndpoint : IPostEndpoint<WebhookEndpointChange>
{
    public static string Path => "/webhooks/endpoints/update";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookEndpointChange request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.UpdateAsync(request ?? new(null, null, null, null), userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/endpoints/delete</c>.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class DeleteWebhookEndpoint : IPostEndpoint<WebhookIdBody>
{
    public static string Path => "/webhooks/endpoints/delete";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.DeleteAsync(request?.Id, userId, cancellationToken), _ => TypedResults.NoContent())
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/endpoints/rotate-secret</c>: a new secret (shown once); the old one signs alongside for a day.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class RotateWebhookSecret : IPostEndpoint<WebhookIdBody>
{
    public static string Path => "/webhooks/endpoints/rotate-secret";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.RotateSecretAsync(request?.Id, userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/endpoints/enable</c>: re-enables a disabled webhook and resumes a halted ordered chain.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class EnableWebhookEndpoint : IPostEndpoint<WebhookIdBody>
{
    public static string Path => "/webhooks/endpoints/enable";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.EnableAsync(request?.Id, userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/endpoints/ping</c>: sends a <c>ping</c> event; the answer is the delivery's id.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class PingWebhookEndpoint : IPostEndpoint<WebhookIdBody>
{
    public static string Path => "/webhooks/endpoints/ping";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.PingAsync(request?.Id, userId, cancellationToken), id => TypedResults.Ok(new WebhookIdBody(id)))
            : TypedResults.Unauthorized();
}

/// <summary><c>GET /spark/apps/webhooks/deliveries?endpointId=…&amp;after=…</c>: the delivery log, newest first, 50 at a time.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class ListWebhookDeliveries : IGetEndpoint
{
    public static string Path => "/webhooks/deliveries";

    [QueryParam("endpointId")] public string? EndpointId { get; set; }
    [QueryParam("after")] public string? After { get; set; }

    [Inject] private readonly WebhookEndpointManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? AppsHttp.ToResult(await manager.ListDeliveriesAsync(EndpointId, After, 50, userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/webhooks/deliveries/redeliver</c>: sends a logged delivery again with the same <c>webhook-id</c>.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class RedeliverWebhook : IPostEndpoint<WebhookIdBody>
{
    public static string Path => "/webhooks/deliveries/redeliver";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly WebhookEndpointManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WebhookIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.RedeliverAsync(request?.Id, userId, cancellationToken), id => TypedResults.Ok(new WebhookIdBody(id)))
            : TypedResults.Unauthorized();
}
