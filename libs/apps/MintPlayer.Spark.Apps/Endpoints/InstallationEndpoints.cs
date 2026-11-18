using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Services;

namespace MintPlayer.Spark.Apps.Endpoints;

public sealed record AppInstallationIdBody(string? Id);

/// <summary><c>GET /spark/apps/installations/new?slug=…</c>: the consent screen: what the App asks for, and where the user may install it.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class GetAppInstallPrompt : IGetEndpoint
{
    public static string Path => "/installations/new";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    [Inject] private readonly AppInstallationManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? AppsHttp.ToResult(await manager.PromptAsync(httpContext.Request.Query["slug"], userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/installations</c>: installs the App on a target the user manages, granting what it asks for now.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class InstallSparkApp : IPostEndpoint<AppInstallInput>
{
    public static string Path => "/installations";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly AppInstallationManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AppInstallInput request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.InstallAsync(request ?? new(null, null, null), userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>GET /spark/apps/installations?targetKind=…&amp;targetKey=…</c>: the Apps installed on a target.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class ListAppInstallations : IGetEndpoint
{
    public static string Path => "/installations";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    [Inject] private readonly AppInstallationManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (AppsHttp.UserId(httpContext) is not { } userId)
            return TypedResults.Unauthorized();
        var query = httpContext.Request.Query;
        return AppsHttp.ToResult(await manager.ListForTargetAsync(query["targetKind"], query["targetKey"], userId, httpContext.RequestAborted));
    }
}

/// <summary><c>GET /spark/apps/apps/installations?slug=…</c>: an App's installations, to its managers.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class ListSparkAppInstallations : IGetEndpoint
{
    public static string Path => "/apps/installations";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    [Inject] private readonly AppInstallationManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => AppsHttp.UserId(httpContext) is { } userId
            ? AppsHttp.ToResult(await manager.ListForAppAsync(httpContext.Request.Query["slug"], userId, httpContext.RequestAborted))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/installations/suspend</c>: no events, and its tokens stop working.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class SuspendAppInstallation : IPostEndpoint<AppInstallationIdBody>
{
    public static string Path => "/installations/suspend";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly AppInstallationManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AppInstallationIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.SetSuspendedAsync(request?.Id, true, userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/installations/unsuspend</c>.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class UnsuspendAppInstallation : IPostEndpoint<AppInstallationIdBody>
{
    public static string Path => "/installations/unsuspend";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly AppInstallationManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AppInstallationIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.SetSuspendedAsync(request?.Id, false, userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/installations/accept</c>: grants what the App asks for now.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class AcceptAppInstallationPermissions : IPostEndpoint<AppInstallationIdBody>
{
    public static string Path => "/installations/accept";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly AppInstallationManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AppInstallationIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.AcceptPermissionsAsync(request?.Id, userId, cancellationToken))
            : TypedResults.Unauthorized();
}

/// <summary><c>POST /spark/apps/installations/delete</c>: uninstalls; its tokens are revoked.</summary>
[MemberOf<AppsGroup>]
internal sealed partial class UninstallSparkApp : IPostEndpoint<AppInstallationIdBody>
{
    public static string Path => "/installations/delete";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAppsFeature.IsOn(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => AppsHttp.RequireAntiforgery(builder);

    [Inject] private readonly AppInstallationManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AppInstallationIdBody request, CancellationToken cancellationToken)
        => AppsHttp.UserId(httpContextAccessor.HttpContext!) is { } userId
            ? AppsHttp.ToResult(await manager.UninstallAsync(request?.Id, userId, cancellationToken), _ => TypedResults.NoContent())
            : TypedResults.Unauthorized();
}
