using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Services;

namespace MintPlayer.Spark.Apps.Endpoints;

/// <summary>Registered by <see cref="AppsBuilder.EnableApps"/>; the App endpoints are mapped only when it is.</summary>
internal sealed class SparkAppsFeature
{
    public static bool IsOn(IServiceProvider services) => services.GetService(typeof(SparkAppsFeature)) is not null;
}

public sealed record SparkAppSlugBody(string? Slug);

public sealed record SparkAppKeyBody(string? Slug, string? KeyId);

/// <summary><c>GET /spark/apps/apps?ownerKind=…&amp;ownerKey=…</c>: the owner's Apps.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class ListSparkApps : IGetEndpoint
{
    public static string Path => "/apps";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    [QueryParam("ownerKind")] public string? OwnerKind { get; set; }
    [QueryParam("ownerKey")] public string? OwnerKey { get; set; }

    [Inject] private readonly SparkAppManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? AppsHttp.ToResult(await manager.ListAsync(OwnerKind, OwnerKey, userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>GET /spark/apps/apps/app?slug=…</c>: the full App to its managers, the public view of a public App to anyone else.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class GetSparkApp : IGetEndpoint
{
    public static string Path => "/apps/app";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    [QueryParam("slug")] public string? Slug { get; set; }

    [Inject] private readonly SparkAppManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? AppsHttp.ToResult(await manager.GetAsync(Slug, userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/apps</c>: registers an App; the answer holds its webhook secret, shown this once.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class CreateSparkApp : IPostEndpoint<SparkAppInput>
{
    public static string Path => "/apps";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly SparkAppManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkAppInput request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.CreateAsync(request ?? new(null, null, null, null, null, null, null, null, null, null, null), userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/apps/update</c>: null leaves a field as it is; an empty webhook URL removes the webhook.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class UpdateSparkApp : IPostEndpoint<SparkAppChange>
{
    public static string Path => "/apps/update";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly SparkAppManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkAppChange request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.UpdateAsync(request ?? new(null, null, null, null, null, null, null, null, null), userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/apps/delete</c>: the App, its webhook and its client (tokens revoked).</summary>
[MemberOf<AppsGroup>]
internal sealed partial class DeleteSparkApp : IPostEndpoint<SparkAppSlugBody>
{
    public static string Path => "/apps/delete";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly SparkAppManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkAppSlugBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.DeleteAsync(request?.Slug, userId, cancellationToken), _ => TypedResults.NoContent())
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/apps/keys/generate</c>: a new private key (PEM), shown this once.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class GenerateSparkAppKey : IPostEndpoint<SparkAppSlugBody>
{
    public static string Path => "/apps/keys/generate";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly SparkAppManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkAppSlugBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.GenerateKeyAsync(request?.Slug, userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/apps/keys/delete</c>.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class DeleteSparkAppKey : IPostEndpoint<SparkAppKeyBody>
{
    public static string Path => "/apps/keys/delete";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly SparkAppManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkAppKeyBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.DeleteKeyAsync(request?.Slug, request?.KeyId, userId, cancellationToken))
            : TypedResults.Unauthorized();
}
