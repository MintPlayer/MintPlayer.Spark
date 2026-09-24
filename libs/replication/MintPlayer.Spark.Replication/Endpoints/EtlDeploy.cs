using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

internal sealed partial class EtlDeploy : IPostEndpoint, IMemberOf<SparkEtlGroup>
{
    public static string Path => "/deploy";

    // ⚠️ Deliberately NO RequireAntiforgeryTokenAttribute — same reasoning as SyncApply, and this is
    // the endpoint where it was most tempting to keep one: it deploys RavenDB ETL tasks from a
    // caller-supplied JavaScript transform and target URL, which is arbitrary code against the
    // database pointed anywhere.
    //
    // It is still the right call. Explicit metadata is enforced before authentication and applies to
    // anonymous callers, so it converted "no module certificate" into a bare 400 and hid the actual
    // refusal. And since 11.0.0 RequireAntiforgery defaults to true, so an ambient-credentialed
    // caller — the browser this was protecting against — is checked by the default branch anyway,
    // without depending on the hosting application naming a path prefix.

    [Inject] private readonly ILogger<EtlTaskManager> logger;
    [Inject] private readonly EtlTaskManager etlTaskManager;
    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        EtlScriptRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<EtlScriptRequest>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invalid ETL deployment request body");
            return Results.BadRequest(new EtlDeploymentResult
            {
                Success = false,
                Error = "Invalid request body"
            });
        }

        if (request == null || request.Scripts == null || request.Scripts.Count == 0)
        {
            return Results.BadRequest(new EtlDeploymentResult
            {
                Success = false,
                Error = "Request must contain at least one script"
            });
        }

        // R2-C1: mTLS gate. Validate the client cert matches the pinned thumbprint
        // for request.RequestingModule before invoking the ETL deployment — this
        // endpoint puts attacker-controlled JS transforms and target URLs into
        // RavenDB ETL pipelines, so authentication is non-negotiable.
        var certValidation = await certificateValidator.ValidateAsync(httpContext, request.RequestingModule ?? string.Empty, httpContext.RequestAborted);
        switch (certValidation)
        {
            case ModuleCertificateValidation.MissingCertificate:
                return Results.Json(new EtlDeploymentResult
                {
                    Success = false,
                    Error = "Client certificate required"
                }, statusCode: 401);
            case ModuleCertificateValidation.ThumbprintMismatch:
            case ModuleCertificateValidation.UnknownModule:
                logger.LogWarning(
                    "ETL deployment refused: certificate validation failed for module '{Module}' ({Reason})",
                    request.RequestingModule, certValidation);
                return Results.Json(new EtlDeploymentResult
                {
                    Success = false,
                    Error = "Forbidden"
                }, statusCode: 403);
        }

        httpContext.EstablishModuleIdentity(request.RequestingModule ?? string.Empty);

        // Read authorization. The certificate answers "who are you"; it has never answered "what
        // may you read". Until now the mTLS gate was binary, so any module holding a valid pinned
        // certificate could ask the owner to push ANY collection — SparkUsers included — into a
        // database it controls, continuously, via a caller-supplied JS transform. The owner had no
        // declared notion of what it was willing to share: [Replicated] lives on the *consumer* and
        // is never consulted here.
        foreach (var script in request.Scripts)
        {
            if (!await permissionService.IsAllowedAsync("Replicate", script.SourceCollection))
            {
                logger.LogWarning(
                    "ETL deployment refused: module '{Module}' may not replicate collection '{Collection}'.",
                    request.RequestingModule, script.SourceCollection);

                return Results.Json(new EtlDeploymentResult
                {
                    Success = false,
                    Error = "Forbidden",
                }, statusCode: 403);
            }
        }

        var result = await etlTaskManager.DeployAsync(request);

        return result.Success ? Results.Ok(result) : Results.StatusCode(500);
    }
}
