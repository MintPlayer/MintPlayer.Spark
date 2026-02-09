using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

internal static class EtlEndpoints
{
    /// <summary>
    /// Handles POST /spark/etl/deploy — receives ETL script requests from other modules
    /// and creates/updates RavenDB ETL tasks in this module's database.
    /// </summary>
    public static async Task<IResult> HandleDeployAsync(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<EtlTaskManager>>();

        EtlScriptRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<EtlScriptRequest>();
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

        var etlTaskManager = context.RequestServices.GetRequiredService<EtlTaskManager>();
        var result = await etlTaskManager.DeployAsync(request);

        return result.Success ? Results.Ok(result) : Results.StatusCode(500);
    }
}
