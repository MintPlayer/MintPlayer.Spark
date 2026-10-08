using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[MemberOf<LookupReferencesGroup>]
internal sealed partial class AddLookupReferenceValue : IPostEndpoint<LookupReferenceValueDto>
{
    public static string Path => "/{name}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [RouteParam] public string Name { get; set; } = "";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly ILogger<AddLookupReferenceValue> logger;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => LookupReferenceBodies.BindFailedAsync(context, permissionService, failure);

    public override async Task<IResult> HandleAsync(LookupReferenceValueDto value, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;

        try
        {
            // R2-H4: gate mutations behind Edit/LookupReferences. Round 1's route
            // inventory marked these "Yes*" but no permission check existed in code
            // or service. Apps grant this in security.json to admin tiers only.
            await permissionService.EnsureAuthorizedAsync("Edit", "LookupReferences");

            var result = await lookupReferenceService.AddValueAsync(Name, value);
            return Results.Json(result, statusCode: 201);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
        catch (InvalidOperationException ex)
        {
            // R2-M1: don't echo ex.Message — leaks RavenDB-internal strings,
            // index/collection names, etc. Log server-side with correlation ID.
            logger.LogWarning(ex, "AddLookupReferenceValue failed");
            return Results.Json(new { error = "Operation failed" }, statusCode: 400);
        }
    }
}

/// <summary>What the lookup-reference mutations answer a body they cannot bind.</summary>
internal static class LookupReferenceBodies
{
    /// <summary>
    /// The answers of the hand-read body (measured before the endpoints became typed, endpoints
    /// generator completion M3): a caller without <c>Edit/LookupReferences</c> is refused first, as the
    /// permission check used to run before the body was read; a JSON <c>null</c> is "Invalid request
    /// body"; anything else is "Operation failed". The one change: an empty or malformed JSON body used
    /// to escape as an unhandled <c>JsonException</c> (a 500), and is now that same 400.
    /// </summary>
    public static async ValueTask<IResult> BindFailedAsync(HttpContext context, IPermissionService permissionService, EndpointBindingException? failure)
    {
        try
        {
            await permissionService.EnsureAuthorizedAsync("Edit", "LookupReferences");
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(context);
        }

        return Results.Json(new { error = failure is null ? "Invalid request body" : "Operation failed" }, statusCode: 400);
    }
}
