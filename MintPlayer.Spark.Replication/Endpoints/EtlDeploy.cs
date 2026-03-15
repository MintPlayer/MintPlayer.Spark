using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

        var result = await etlTaskManager.DeployAsync(request);

        return result.Success ? Results.Ok(result) : Results.StatusCode(500);
    }
}
