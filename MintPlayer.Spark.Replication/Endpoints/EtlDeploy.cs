using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class EtlDeploy : IEndpoint
{
    [Inject] private readonly ILogger<EtlTaskManager> logger;
    [Inject] private readonly EtlTaskManager etlTaskManager;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/deploy", async (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<EtlDeploy>();
            return await endpoint.HandleAsync(context);
        });
    }

    public async Task<IResult> HandleAsync(HttpContext context)
    {
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

        var result = await etlTaskManager.DeployAsync(request);

        return result.Success ? Results.Ok(result) : Results.StatusCode(500);
    }
}
