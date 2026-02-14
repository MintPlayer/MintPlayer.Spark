using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Endpoints;

internal static class SyncEndpoints
{
    /// <summary>
    /// Handles POST /spark/sync/apply — receives sync action requests from non-owner modules
    /// and applies the CRUD operations on the locally owned entities via the actions pipeline.
    /// </summary>
    public static async Task<IResult> HandleApplyAsync(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SparkSync");

        SyncActionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<SyncActionRequest>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invalid sync action request body");
            return Results.BadRequest(new { error = "Invalid request body" });
        }

        if (request == null || request.Actions == null || request.Actions.Count == 0)
        {
            return Results.BadRequest(new { error = "Request must contain at least one sync action" });
        }

        var handler = context.RequestServices.GetService<ISyncActionHandler>();
        if (handler == null)
        {
            logger.LogError("ISyncActionHandler is not registered. Ensure MintPlayer.Spark is configured.");
            return Results.StatusCode(500);
        }

        var results = new List<SyncActionResult>();

        foreach (var action in request.Actions)
        {
            try
            {
                switch (action.ActionType)
                {
                    case SyncActionType.Insert:
                    case SyncActionType.Update:
                        if (action.Data == null)
                        {
                            results.Add(new SyncActionResult
                            {
                                Collection = action.Collection,
                                DocumentId = action.DocumentId,
                                Success = false,
                                Error = "Data is required for Insert and Update actions"
                            });
                            continue;
                        }

                        var savedId = await handler.HandleSaveAsync(
                            action.Collection, action.DocumentId, action.Data.Value, action.Properties);

                        results.Add(new SyncActionResult
                        {
                            Collection = action.Collection,
                            DocumentId = savedId ?? action.DocumentId,
                            Success = true
                        });
                        break;

                    case SyncActionType.Delete:
                        if (string.IsNullOrEmpty(action.DocumentId))
                        {
                            results.Add(new SyncActionResult
                            {
                                Collection = action.Collection,
                                DocumentId = action.DocumentId,
                                Success = false,
                                Error = "DocumentId is required for Delete actions"
                            });
                            continue;
                        }

                        await handler.HandleDeleteAsync(action.Collection, action.DocumentId);

                        results.Add(new SyncActionResult
                        {
                            Collection = action.Collection,
                            DocumentId = action.DocumentId,
                            Success = true
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply sync action {ActionType} on {Collection}/{DocumentId}",
                    action.ActionType, action.Collection, action.DocumentId);

                results.Add(new SyncActionResult
                {
                    Collection = action.Collection,
                    DocumentId = action.DocumentId,
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        var allSucceeded = results.All(r => r.Success);

        logger.LogInformation(
            "Processed {Count} sync action(s) from '{RequestingModule}': {Succeeded} succeeded, {Failed} failed",
            request.Actions.Count, request.RequestingModule,
            results.Count(r => r.Success), results.Count(r => !r.Success));

        return allSucceeded
            ? Results.Ok(new { results })
            : Results.Json(new { results }, statusCode: 207); // 207 Multi-Status for partial success
    }
}

internal class SyncActionResult
{
    public required string Collection { get; set; }
    public string? DocumentId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
