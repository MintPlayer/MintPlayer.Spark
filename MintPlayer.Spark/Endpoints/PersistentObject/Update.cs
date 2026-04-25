using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class UpdatePersistentObject : IPutEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/{**id}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Entity type '{objectTypeId}' not found" }, 404);
        }

        try
        {
            var decodedId = Uri.UnescapeDataString(id);
            var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (existingObj is null)
            {
                return ClientResult.Envelope(clientAccessor, new { error = $"Object with ID {decodedId} not found" }, 404);
            }

            var request = await httpContext.Request.ReadFromJsonAsync<PersistentObjectRequest>()
                ?? throw new InvalidOperationException("Request could not be deserialized from the request body.");

            var obj = request.PersistentObject
                ?? throw new InvalidOperationException("PersistentObject is required.");

            if (request.RetryResults is { Length: > 0 } retryResults)
            {
                var accessor = (RetryAccessor)retryAccessor;
                accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
            }

            obj.Id = existingObj.Id;
            obj.ObjectTypeId = entityType.Id;

            var validationResult = validationService.Validate(obj);
            if (!validationResult.IsValid)
            {
                return ClientResult.Envelope(clientAccessor, new { errors = validationResult.Errors }, 400);
            }

            var result = await databaseAccess.SavePersistentObjectAsync(obj);
            return ClientResult.Envelope(clientAccessor, result, 200);
        }
        catch (SparkConcurrencyException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { error = ex.Message }, 409);
        }
        catch (SparkRetryActionException ex)
        {
            return ClientResult.Retry(clientAccessor, ex);
        }
        catch (SparkAccessDeniedException)
        {
            var isAuthed = httpContext.User.Identity?.IsAuthenticated == true;
            return ClientResult.Envelope(clientAccessor,
                new { error = isAuthed ? "Access denied" : "Authentication required" },
                isAuthed ? 403 : 401);
        }
    }
}
