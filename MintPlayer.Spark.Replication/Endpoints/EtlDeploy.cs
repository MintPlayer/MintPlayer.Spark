using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

internal sealed partial class EtlDeploy : IPostEndpoint, IMemberOf<SparkEtlGroup>
{
    public static string Path => "/deploy";

    [Inject] private readonly ILogger<EtlTaskManager> logger;
    [Inject] private readonly EtlTaskManager etlTaskManager;
    [Inject] private readonly IModuleCertificateValidator certificateValidator;

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

        var result = await etlTaskManager.DeployAsync(request);

        return result.Success ? Results.Ok(result) : Results.StatusCode(500);
    }
}
