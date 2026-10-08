using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation;

namespace QnA.Testing;

/// <summary>
/// <c>/qna-test/moderation</c>: the test seams (<see cref="QnATestSeams"/>). The whole group exists only
/// when <c>QnA:TestSeams:Enabled</c> is true, and every endpoint in it needs the antiforgery token.
/// </summary>
public sealed class QnATestSeamsGroup : IEndpointGroup
{
    public static string Prefix => "/qna-test/moderation";

    /// <summary>
    /// Read once, at map time, from the root provider's configuration and environment. Throws there,
    /// so at startup, when the seams are configured on in Production.
    /// </summary>
    static bool IEndpointGroup.IsEnabled(IServiceProvider services)
        => QnATestSeams.IsEnabled(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>());

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    /// <summary>Only a moderator gets an answer other than 404 from a seam.</summary>
    internal static Task<bool> IsModeratorAsync(IPermissionService permissions)
        => permissions.IsAllowedAsync(ModerationRights.Audit, ModerationRights.Target);
}

/// <summary><c>POST /qna-test/moderation/credit</c>: runs the crediting job now and answers how many entries it credited.</summary>
[MemberOf<QnATestSeamsGroup>]
internal sealed partial class CreditSeam : IPostEndpoint
{
    public static string Path => "/credit";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkModerationJobs jobs;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => await QnATestSeamsGroup.IsModeratorAsync(permissions)
            ? Results.Json(new { credited = await jobs.RunCreditingAsync(httpContext.RequestAborted) })
            : Results.NotFound();
}

/// <summary><c>POST /qna-test/moderation/detect</c>: runs the fraud detector now and answers its report.</summary>
[MemberOf<QnATestSeamsGroup>]
internal sealed partial class DetectSeam : IPostEndpoint
{
    public static string Path => "/detect";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkModerationJobs jobs;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => await QnATestSeamsGroup.IsModeratorAsync(permissions)
            ? Results.Json(await jobs.RunFraudDetectorAsync(httpContext.RequestAborted))
            : Results.NotFound();
}

/// <summary>The body of <see cref="RecomputeSeam"/>.</summary>
public sealed record RecomputeRequest(List<string>? UserIds);

/// <summary><c>POST /qna-test/moderation/recompute</c>: recomputes the named users' reputation now.</summary>
[MemberOf<QnATestSeamsGroup>]
internal sealed partial class RecomputeSeam : IPostEndpoint<RecomputeRequest>
{
    public static string Path => "/recompute";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkModerationJobs jobs;

    public override async Task<IResult> HandleAsync(RecomputeRequest request, CancellationToken cancellationToken)
    {
        if (!await QnATestSeamsGroup.IsModeratorAsync(permissions))
            return Results.NotFound();
        await jobs.RecomputeReputationAsync(request.UserIds ?? [], cancellationToken);
        return Results.NoContent();
    }
}
