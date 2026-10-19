using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

[MemberOf<SparkEtlGroup>]
internal sealed partial class EtlDeploy : IPostEndpoint<EtlDeploymentRequest>
{
    public static string Path => "/deploy";

    // ⚠️ Deliberately NO RequireAntiforgeryTokenAttribute — same reasoning as SyncApply.
    //
    // Explicit metadata is enforced before authentication and applies to anonymous callers, so it
    // converted "no module certificate" into a bare 400 and hid the actual refusal. And since
    // 11.0.0 RequireAntiforgery defaults to true, so an ambient-credentialed caller is checked by
    // the default branch anyway, without depending on the hosting application naming a path prefix.
    //
    // What this endpoint used to accept is gone: a caller-supplied JavaScript transform (F16) and a
    // caller-supplied target database and URLs (F19). The owner now generates every transform from
    // its own contracts and takes the destination from the authenticated module's registry entry.

    [Inject] private readonly ILogger<EtlTaskManager> logger;
    [Inject] private readonly IEtlTaskManager etlTaskManager;
    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly ReplicationContractCatalog contractCatalog;

    /// <summary>
    /// A body that cannot be bound answers what the hand-read body did (measured before the endpoint
    /// became typed, endpoints generator completion M3): a JSON <c>null</c> has no contracts, anything
    /// else is an invalid body.
    /// </summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
    {
        if (failure is null)
            return new(NoContracts());

        logger.LogWarning(failure, "Invalid ETL deployment request body");
        return new(Refusal(400, "Invalid request body"));
    }

    private static IResult NoContracts() => Refusal(400, "Request must contain at least one contract");

    private static IResult Refusal(int statusCode, string error) => Results.Json(new EtlDeploymentResult
    {
        Success = false,
        Error = error,
    }, statusCode: statusCode);

    public override async Task<IResult> HandleAsync(EtlDeploymentRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;

        if (request.Contracts == null || request.Contracts.Count == 0)
            return NoContracts();

        // R2-C1: mTLS gate. Validate the client cert matches the pinned thumbprint for
        // request.RequestingModule before anything else: this endpoint creates RavenDB ETL tasks
        // that push the owner's data out, so authentication is non-negotiable.
        var certValidation = await certificateValidator.ValidateAsync(httpContext, request.RequestingModule ?? string.Empty, httpContext.RequestAborted);
        switch (certValidation)
        {
            case ModuleCertificateValidation.MissingCertificate:
                return Refusal(401, "Client certificate required");
            case ModuleCertificateValidation.ThumbprintMismatch:
            case ModuleCertificateValidation.UnknownModule:
                logger.LogWarning(
                    "ETL deployment refused: certificate validation failed for module '{Module}' ({Reason})",
                    request.RequestingModule, certValidation);
                return Refusal(403, "Forbidden");
        }

        httpContext.EstablishModuleIdentity(request.RequestingModule ?? string.Empty);

        var transforms = new List<EtlTransformScript>();
        foreach (var item in request.Contracts)
        {
            // The target collection becomes part of the generated script (loadTo{Collection}), so it
            // must be a plain identifier before it goes anywhere near it.
            if (item is null || !EtlTransformGenerator.IsIdentifier(item.TargetCollection))
                return Refusal(400, "Invalid target collection");

            var contract = contractCatalog.Find(item.Contract, item.Version);
            if (contract is null)
            {
                logger.LogWarning(
                    "ETL deployment refused: module '{Module}' asked for contract '{Contract}' v{Version}, which this module does not publish.",
                    request.RequestingModule, item.Contract, item.Version);
                return Refusal(404, $"Unknown contract '{item.Contract}' v{item.Version}");
            }

            // Read authorization. The certificate answers "who are you"; the grant answers "what may
            // you read". The collection checked is the OWNER's contract source, never a name from the
            // request. (Contract-named grants, Replicate/{contract}, replace this in warehouse WX4.)
            if (!await permissionService.IsAllowedAsync("Replicate", contract.SourceCollection))
            {
                logger.LogWarning(
                    "ETL deployment refused: module '{Module}' may not replicate collection '{Collection}' (contract '{Contract}').",
                    request.RequestingModule, contract.SourceCollection, item.Contract);
                return Refusal(403, "Forbidden");
            }

            transforms.Add(new EtlTransformScript(
                $"{item.Contract}-v{item.Version}-{item.TargetCollection}",
                contract.SourceCollection,
                contract.ScriptFor(item.TargetCollection)));
        }

        // F19: the destination is the authenticated module's registry entry, never the request.
        var registration = await moduleDirectory.FindAsync(request.RequestingModule!, cancellationToken);
        if (registration is null || string.IsNullOrEmpty(registration.DatabaseName) || registration.DatabaseUrls.Length == 0)
        {
            logger.LogWarning(
                "ETL deployment refused: module '{Module}' has no database registered in SparkModules.",
                request.RequestingModule);
            return Refusal(409, "The requesting module has no database registered");
        }

        var result = await etlTaskManager.DeployAsync(new EtlDeployment(
            request.RequestingModule!,
            registration.DatabaseName,
            registration.DatabaseUrls,
            transforms), cancellationToken);

        if (result.Success)
            return Results.Ok(result);

        // F18: a licence without ETL is the owner's configuration, not a transient fault. 409 with
        // its reason, so the consumer dead-letters once instead of retrying.
        return result.Error == EtlDeploymentResult.NotLicensedError
            ? Results.Json(result, statusCode: 409)
            : Results.StatusCode(500);
    }
}
