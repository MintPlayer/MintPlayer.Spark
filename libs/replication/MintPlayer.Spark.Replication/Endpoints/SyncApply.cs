using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

internal sealed partial class SyncApply : IPostEndpoint, IMemberOf<SparkSyncGroup>
{
    public static string Path => "/apply";

    // ⚠️ Deliberately NO RequireAntiforgeryTokenAttribute, and this endpoint briefly carried one.
    //
    // The intent was defence in depth: this endpoint lets the caller mutate or delete any document in
    // any collection, and the only thing standing between a browser and that was one `if` inside the
    // handler. But explicit metadata is enforced BEFORE authentication, and it applies to anonymous
    // callers too — so stamping it turned "you presented no module certificate" (401/403) into a bare
    // 400 for every unauthenticated caller, masking the real reason and breaking the tests that pin
    // it (CrossModuleSyncTests, ReplicationEndpointAuthTests).
    //
    // It is unnecessary as of 11.0.0 regardless. SparkAntiforgeryOptions.RequireAntiforgery now
    // defaults to true, so a caller who reaches here carrying an AMBIENT credential — the browser
    // this was worried about — is checked by the default branch without any annotation. A module
    // presenting its client certificate is non-ambient and exempt either way, and a caller presenting
    // nothing has no authority to forge and should be told so by the auth check rather than by the
    // antiforgery gate.

    [Inject] private readonly ILoggerFactory loggerFactory;
    [Inject] private readonly IModuleCertificateValidator certificateValidator;
    // Nullable fields produce optional ctor params in the generated [Inject] ctor;
    // they must come AFTER any non-nullable (required) fields, otherwise C# rejects
    // the constructor as "optional parameters must appear after all required ones".
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

        // R2-C2: mTLS gate. /spark/sync/apply lets the caller mutate or delete
        // any document in any collection — must require an authenticated module.
        var certValidation = await certificateValidator.ValidateAsync(httpContext, request.RequestingModule ?? string.Empty, httpContext.RequestAborted);
        switch (certValidation)
        {
            case ModuleCertificateValidation.MissingCertificate:
                return Results.Json(new { error = "Client certificate required" }, statusCode: 401);
            case ModuleCertificateValidation.ThumbprintMismatch:
            case ModuleCertificateValidation.UnknownModule:
                logger.LogWarning(
                    "Sync apply refused: certificate validation failed for module '{Module}' ({Reason})",
                    request.RequestingModule, certValidation);
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);
        }

        // Validation established WHO is calling; this is what tells the authorization pipeline.
        // Without it the writes M11 routed through IPermissionService arrive anonymous and are
        // refused for holding only Everyone's rights — the gate says "yes, this is HR" and the
        // permission check never hears about it.
        httpContext.EstablishModuleIdentity(request.RequestingModule ?? string.Empty);

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
