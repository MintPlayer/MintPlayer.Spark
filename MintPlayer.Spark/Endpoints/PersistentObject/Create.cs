using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientInstructions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class CreatePersistentObject : IPostEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}";

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

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Entity type '{objectTypeId}' not found" }, 404);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<PersistentObjectRequest>()
            ?? throw new InvalidOperationException("Request could not be deserialized from the request body.");

        var obj = request.PersistentObject
            ?? throw new InvalidOperationException("PersistentObject is required.");

        // Set up retry state if this is a re-invocation
        if (request.RetryResults is { Length: > 0 } retryResults)
        {
            var accessor = (RetryAccessor)retryAccessor;
            accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
        }

        // Ensure the ObjectTypeId matches the resolved entity type
        obj.ObjectTypeId = entityType.Id;

        // Validate the object
        var validationResult = validationService.Validate(obj);
        if (!validationResult.IsValid)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = validationResult.Errors }, 400);
        }

        try
        {
            var result = await databaseAccess.SavePersistentObjectAsync(obj);
            return ClientResult.Envelope(clientAccessor, result, 201);
        }
        catch (SparkRetryActionException)
        {
            // Retry instruction was already pushed onto clientAccessor by RetryAccessor.Action()
            // before this exception unwound — rides out in the envelope's instructions list.
            return ClientResult.Envelope(clientAccessor, null, 449);
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
