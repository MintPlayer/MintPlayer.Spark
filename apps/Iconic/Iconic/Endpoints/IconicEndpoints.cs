using Iconic.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace Iconic.Endpoints;

// The editor's API under /api/iconic (docs/iconic_PRD.md §6 Stage 0, D25), as generator endpoint classes
// (docs/endpoints_generator_completion_PRD.md R3). Each endpoint is a thin shell over ProjectService, which owns the
// rules. Every state-changing call needs the antiforgery header; saves are rate-limited per user (D21) on top of
// Spark's per-IP limiter for the prefix (IconicRateLimits).

internal sealed class IconicProjectsGroup : IEndpointGroup
{
    public static string Prefix => "/api/iconic/projects";
}

internal sealed class IconicSharedGroup : IEndpointGroup
{
    public static string Prefix => "/api/iconic/shared";
}

internal sealed class IconicAdminGroup : IEndpointGroup
{
    public static string Prefix => "/api/iconic/admin";
}

/// <summary>The metadata the endpoints share.</summary>
internal static class IconicEndpoint
{
    private static readonly RequireAntiforgeryTokenAttribute Csrf = new(true);

    /// <summary>A state-changing call: the antiforgery header.</summary>
    public static RouteHandlerBuilder Mutation(RouteHandlerBuilder builder) => builder.WithMetadata(Csrf);

    /// <summary>A save: antiforgery plus the per-user save rate limit (D21).</summary>
    public static RouteHandlerBuilder Save(RouteHandlerBuilder builder) => Mutation(builder).RequireRateLimiting(IconicRateLimits.SavePolicy);

    /// <summary>A scene save: a body up to 8 MB, so the 5 MB quota answers 413 with its own message.</summary>
    public static RouteHandlerBuilder SceneSave(RouteHandlerBuilder builder) => Save(builder).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));
}

// ---- projects -----------------------------------------------------------------------------------

/// <summary><c>POST /api/iconic/projects</c>: a new project for the caller.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class CreateProjectEndpoint : IPostEndpoint<CreateProjectRequest>
{
    public static string Path => "";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Save(builder);
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(CreateProjectRequest request, CancellationToken cancellationToken)
        => projects.CreateAsync(request, cancellationToken);
}

/// <summary><c>GET /api/iconic/projects</c>: the caller's projects.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class ListProjectsEndpoint : IGetEndpoint
{
    public static string Path => "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.ListAsync(httpContext.RequestAborted);
}

/// <summary><c>GET /api/iconic/projects/{key}</c>: a project with its scene.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class GetProjectEndpoint : IGetEndpoint
{
    public static string Path => "/{key}";
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.GetAsync(Key, httpContext.RequestAborted);
}

/// <summary><c>DELETE /api/iconic/projects/{key}</c>: the project and its scene, owner only.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class DeleteProjectEndpoint : IDeleteEndpoint
{
    public static string Path => "/{key}";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Save(builder);
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.DeleteAsync(Key, httpContext.RequestAborted);
}

/// <summary><c>PUT /api/iconic/projects/{key}/meta</c>: name, kind and design size.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class UpdateProjectEndpoint : IPutEndpoint<UpdateProjectRequest>
{
    public static string Path => "/{key}/meta";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Save(builder);
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(UpdateProjectRequest request, CancellationToken cancellationToken)
        => projects.UpdateAsync(Key, request, cancellationToken);
}

/// <summary><c>PUT /api/iconic/projects/{key}/scene</c>: the whole scene, at a version.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class SaveSceneEndpoint : IPutEndpoint<SaveSceneRequest>
{
    public static string Path => "/{key}/scene";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.SceneSave(builder);
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(SaveSceneRequest request, CancellationToken cancellationToken)
        => projects.SaveSceneAsync(Key, request, cancellationToken);
}

/// <summary><c>PATCH /api/iconic/projects/{key}/scene</c>: patch operations, at a version (S5).</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class PatchSceneEndpoint : IPatchEndpoint<PatchSceneRequest>
{
    public static string Path => "/{key}/scene";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.SceneSave(builder);
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(PatchSceneRequest request, CancellationToken cancellationToken)
        => projects.PatchSceneAsync(Key, request, cancellationToken);
}

// ---- attachments ---------------------------------------------------------------------------------

/// <summary>
/// <c>PUT /api/iconic/projects/{key}/files/{**name}</c>: a paint bitmap, font or thumbnail. The body is raw bytes, not
/// JSON, so there is no typed binding: ProjectService checks the name, the quota and the magic bytes while it reads
/// the body under a cap.
/// </summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class PutFileEndpoint : IPutEndpoint
{
    public static string Path => "/{key}/files/{**name}";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => IconicEndpoint.Save(builder).WithMetadata(new RequestSizeLimitAttribute(11 * 1024 * 1024));
    [RouteParam] public string Key { get; set; } = "";
    [RouteParam] public string Name { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.PutFileAsync(Key, Name, httpContext.Request, httpContext.RequestAborted);
}

/// <summary><c>GET /api/iconic/projects/{key}/files/{**name}</c></summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class GetFileEndpoint : IGetEndpoint
{
    public static string Path => "/{key}/files/{**name}";
    [RouteParam] public string Key { get; set; } = "";
    [RouteParam] public string Name { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.GetFileAsync(Key, Name, httpContext.RequestAborted);
}

/// <summary><c>DELETE /api/iconic/projects/{key}/files/{**name}</c></summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class DeleteFileEndpoint : IDeleteEndpoint
{
    public static string Path => "/{key}/files/{**name}";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Mutation(builder);
    [RouteParam] public string Key { get; set; } = "";
    [RouteParam] public string Name { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.DeleteFileAsync(Key, Name, httpContext.RequestAborted);
}

// ---- snapshots (D22) -----------------------------------------------------------------------------

/// <summary><c>POST /api/iconic/projects/{key}/snapshots</c></summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class CreateSnapshotEndpoint : IPostEndpoint<SnapshotRequest>
{
    public static string Path => "/{key}/snapshots";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Save(builder);
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(SnapshotRequest request, CancellationToken cancellationToken)
        => projects.CreateSnapshotAsync(Key, request, cancellationToken);
}

/// <summary><c>POST /api/iconic/projects/{key}/snapshots/{snapshotId}/restore</c>: snapshots the current state first.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class RestoreSnapshotEndpoint : IPostEndpoint
{
    public static string Path => "/{key}/snapshots/{snapshotId}/restore";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Save(builder);
    [RouteParam] public string Key { get; set; } = "";
    [RouteParam] public string SnapshotId { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.RestoreSnapshotAsync(Key, SnapshotId, httpContext.RequestAborted);
}

/// <summary><c>DELETE /api/iconic/projects/{key}/snapshots/{snapshotId}</c></summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class DeleteSnapshotEndpoint : IDeleteEndpoint
{
    public static string Path => "/{key}/snapshots/{snapshotId}";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Mutation(builder);
    [RouteParam] public string Key { get; set; } = "";
    [RouteParam] public string SnapshotId { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.DeleteSnapshotAsync(Key, SnapshotId, httpContext.RequestAborted);
}

// ---- sharing (D20) -------------------------------------------------------------------------------

/// <summary><c>POST /api/iconic/projects/{key}/share</c>: turn the link on or off, or replace it.</summary>
[MemberOf<IconicProjectsGroup>]
internal sealed partial class ShareProjectEndpoint : IPostEndpoint<ShareRequest>
{
    public static string Path => "/{key}/share";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Mutation(builder);
    [RouteParam] public string Key { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(ShareRequest request, CancellationToken cancellationToken)
        => projects.ShareAsync(Key, request, cancellationToken);
}

/// <summary><c>GET /api/iconic/shared/{token}</c>: a shared drawing, read-only, for anyone holding the link.</summary>
[MemberOf<IconicSharedGroup>]
internal sealed partial class GetSharedEndpoint : IGetEndpoint
{
    public static string Path => "/{token}";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();
    [RouteParam] public string Token { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.GetSharedAsync(Token, httpContext.RequestAborted);
}

/// <summary><c>GET /api/iconic/shared/{token}/files/{**name}</c></summary>
[MemberOf<IconicSharedGroup>]
internal sealed partial class GetSharedFileEndpoint : IGetEndpoint
{
    public static string Path => "/{token}/files/{**name}";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();
    [RouteParam] public string Token { get; set; } = "";
    [RouteParam] public string Name { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.GetSharedFileAsync(Token, Name, httpContext.RequestAborted);
}

/// <summary><c>POST /api/iconic/shared/{token}/remix</c>: a copy in the caller's account.</summary>
[MemberOf<IconicSharedGroup>]
internal sealed partial class RemixEndpoint : IPostEndpoint
{
    public static string Path => "/{token}/remix";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Save(builder);
    [RouteParam] public string Token { get; set; } = "";
    [Inject] private readonly ProjectService projects;

    public Task<IResult> HandleAsync(HttpContext httpContext) => projects.RemixAsync(Token, httpContext.RequestAborted);
}

/// <summary><c>POST /api/iconic/shared/{token}/report</c>: mails the administrators a takedown link.</summary>
[MemberOf<IconicSharedGroup>]
internal sealed partial class ReportSharedEndpoint : IPostEndpoint<ReportRequest>
{
    public static string Path => "/{token}/report";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => IconicEndpoint.Mutation(builder.AllowAnonymous()).RequireRateLimiting(IconicRateLimits.ReportPolicy);
    [RouteParam] public string Token { get; set; } = "";
    [Inject] private readonly ProjectService projects;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(ReportRequest request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!.Request;
        return projects.ReportAsync(Token, request, configuration["Iconic:PublicBaseUrl"] ?? $"{http.Scheme}://{http.Host}", cancellationToken);
    }
}

/// <summary><c>POST /api/iconic/admin/takedown</c>: administrators only (ProjectService checks).</summary>
[MemberOf<IconicAdminGroup>]
internal sealed partial class TakedownEndpoint : IPostEndpoint<TakedownRequest>
{
    public static string Path => "/takedown";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => IconicEndpoint.Mutation(builder);
    [Inject] private readonly ProjectService projects;

    public override Task<IResult> HandleAsync(TakedownRequest request, CancellationToken cancellationToken)
        => projects.TakedownAsync(request, cancellationToken);
}
