using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

/// <summary>
/// <c>POST /spark/replicas/subscribe</c> (owner): a consumer registers the contracts it reads (warehouse WX2,
/// replacing <c>/spark/etl/deploy</c>). The request names contracts and versions only: no script (F16), no
/// destination (F19). Grants are per contract: <c>Replicate/{contract}</c>.
/// </summary>
[MemberOf<SparkReplicasGroup>]
internal sealed partial class ReplicaSubscribe : IPostEndpoint<ReplicaSubscriptionRequest>
{
    public static string Path => "/subscribe";

    [Inject] private readonly ILogger<ReplicaSubscribe> logger;
    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IReplicaSubscriptions subscriptions;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
    {
        if (failure is not null)
            logger.LogWarning(failure, "Invalid replica subscription request body");
        return new(Results.Json(new ReplicaSubscriptionResult { Error = "Invalid request body" }, statusCode: 400));
    }

    public override async Task<IResult> HandleAsync(ReplicaSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (request.Contracts is null)
            return Results.Json(new ReplicaSubscriptionResult { Error = "Invalid request body" }, statusCode: 400);

        // This endpoint makes the owner's data flow out, so authentication comes before anything else.
        if (await ModuleCaller.AuthenticateAsync(certificateValidator, httpContext, request.RequestingModule, logger, "Replica subscription") is { } refusal)
            return refusal;

        var outcome = await subscriptions.SubscribeAsync(
            request.RequestingModule,
            request.Contracts,
            contract => permissionService.IsAllowedAsync(ModuleGrants.ReplicateVerb, contract, cancellationToken),
            cancellationToken);
        return Results.Json(outcome.Result, statusCode: outcome.StatusCode);
    }
}
