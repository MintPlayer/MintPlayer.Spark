using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

// ⚠️ Deliberately no RequireAntiforgeryTokenAttribute on any of these (see ModuleCaller): explicit antiforgery metadata
// is enforced before authentication and turned "no module certificate" into a bare 400. A module presenting its client
// certificate is non-ambient; an ambient-credentialed caller is checked by the default branch anyway (11.0.0).

/// <summary>
/// Owner: <c>POST /spark/replicas/register</c> (warehouse §4.2.2). The consumer lists every replica it reads from this
/// module; the owner re-renders each script, checks the grant and the attribute read rights, records one request per new
/// shape (approved at once only when this host is Development) and withdraws what the post no longer lists.
/// </summary>
[MemberOf<SparkReplicasGroup>]
internal sealed partial class ReplicaRegister : IPostEndpoint<ReplicaRegistrationRequest>
{
    public static string Path => "/register";

    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly ILogger<ReplicaRegister> logger;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
    {
        logger.LogWarning(failure, "Invalid replica registration body");
        return new(Results.Json(new ReplicaRegistrationResult { Error = "Invalid request body" }, statusCode: 400));
    }

    public override async Task<IResult> HandleAsync(ReplicaRegistrationRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (await ModuleCaller.AuthenticateAsync(certificateValidator, httpContext, request.RequestingModule, logger, "Replica registration") is { } refused)
            return refused;

        var outcome = await subscriptions.RegisterAsync(request.RequestingModule, request.Items ?? [], cancellationToken);
        return Results.Json(outcome.Result, statusCode: outcome.StatusCode);
    }
}

/// <summary>
/// Consumer: <c>POST /spark/replicas/apply</c> (warehouse §4.2.3). The caller must be a source module of the target
/// collection, and the hash the consumer's current one or the one last acknowledged as approved (Q-W29).
/// </summary>
[MemberOf<SparkReplicasGroup>]
internal sealed partial class ReplicaApply : IPostEndpoint<ReplicaApplyRequest>
{
    public static string Path => "/apply";

    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IReplicaApplier applier;
    [Inject] private readonly ILogger<ReplicaApply> logger;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
    {
        logger.LogWarning(failure, "Invalid replica push body");
        return new(Results.BadRequest(new { error = "Invalid request body" }));
    }

    public override async Task<IResult> HandleAsync(ReplicaApplyRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (await ModuleCaller.AuthenticateAsync(certificateValidator, httpContext, request.SourceModule, logger, "Replica push") is { } refused)
            return refused;

        var (result, refusal) = await applier.ApplyAsync(request.SourceModule, request, cancellationToken);
        return refusal is null
            ? Results.Ok(result)
            : Results.Json(new { error = refusal.Error }, statusCode: refusal.StatusCode);
    }
}

/// <summary>
/// Consumer: <c>GET /spark/replicas/digest?module=&amp;collection=&amp;after=&amp;take=</c>, the page the owner's reconcile
/// compares with its own documents (warehouse §4.2.4).
/// </summary>
[MemberOf<SparkReplicasGroup>]
internal sealed partial class ReplicaDigest : IGetEndpoint
{
    public static string Path => "/digest";

    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IReplicaApplier applier;
    [Inject] private readonly ILogger<ReplicaDigest> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var query = httpContext.Request.Query;
        var module = query["module"].ToString();
        if (await ModuleCaller.AuthenticateAsync(certificateValidator, httpContext, module, logger, "Replica digest") is { } refused)
            return refused;

        var take = int.TryParse(query["take"].ToString(), out var parsed) ? parsed : 500;
        var (page, refusal) = await applier.DigestAsync(module, query["collection"].ToString(), query["after"].ToString(), take, httpContext.RequestAborted);
        return refusal is null
            ? Results.Ok(page)
            : Results.Json(new { error = refusal.Error }, statusCode: refusal.StatusCode);
    }
}

/// <summary>
/// Owner: <c>POST /spark/sync/apply</c> (warehouse §4.2.5), one write-back per request: 200 applied, 409 conflict,
/// 403 forbidden, 422 invalid, 400 otherwise; never 207 (F12).
/// </summary>
[MemberOf<SparkSyncGroup>]
internal sealed partial class SyncApply : IPostEndpoint<SyncApplyRequest>
{
    public static string Path => "/apply";

    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ISyncApplyService applyService;
    [Inject] private readonly ILogger<SyncApply> logger;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
    {
        logger.LogWarning(failure, "Invalid write-back body");
        return new(Results.Json(new SyncApplyResult { Outcome = SyncApplyOutcome.Refused, Reason = "Invalid request body" }, statusCode: 400));
    }

    public override async Task<IResult> HandleAsync(SyncApplyRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        // R2-C2: the caller must be the module whose pinned certificate it presents; that identity is what the
        // authorization pipeline then judges (the module's type and attribute rights, F17).
        if (await ModuleCaller.AuthenticateAsync(certificateValidator, httpContext, request.RequestingModule, logger, "Write-back") is { } refused)
            return refused;

        var (status, result) = await applyService.ApplyAsync(request.RequestingModule, request, cancellationToken);
        return Results.Json(result, statusCode: status);
    }
}
