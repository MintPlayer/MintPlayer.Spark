using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Endpoints;

internal sealed partial class SyncApply : IPostEndpoint, IMemberOf<SparkSyncGroup>
{
    public static string Path => "/apply";

    [Inject] private readonly ILoggerFactory loggerFactory;
    [Inject] private readonly ISyncActionHandler? syncActionHandler;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var logger = loggerFactory.CreateLogger("SparkSync");

        SyncActionRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<SyncActionRequest>();
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

        if (syncActionHandler == null)
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

                        var savedId = await syncActionHandler.HandleSaveAsync(
                            action.Collection, action.DocumentId, action.Data, action.Properties);

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

                        await syncActionHandler.HandleDeleteAsync(action.Collection, action.DocumentId);

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
