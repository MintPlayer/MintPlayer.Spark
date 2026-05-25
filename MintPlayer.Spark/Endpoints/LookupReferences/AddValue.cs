using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

internal sealed partial class AddLookupReferenceValue : IPostEndpoint, IMemberOf<LookupReferencesGroup>
{
    public static string Path => "/{name}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var name = (string)httpContext.Request.RouteValues["name"]!;

        try
        {
            // R2-H4: gate mutations behind Edit/LookupReferences. Round 1's route
            // inventory marked these "Yes*" but no permission check existed in code
            // or service. Apps grant this in security.json to admin tiers only.
            await permissionService.EnsureAuthorizedAsync("Edit", "LookupReferences");

            var value = await httpContext.Request.ReadFromJsonAsync<LookupReferenceValueDto>();

            if (value == null)
            {
                return Results.Json(new { error = "Invalid request body" }, statusCode: 400);
            }

            var result = await lookupReferenceService.AddValueAsync(name, value);
            return Results.Json(result, statusCode: 201);
        }
        catch (SparkAccessDeniedException)
        {
            var isAuthed = httpContext.User.Identity?.IsAuthenticated == true;
            return Results.Json(
                new { error = isAuthed ? "Access denied" : "Authentication required" },
                statusCode: isAuthed ? 403 : 401);
        }
        catch (InvalidOperationException ex)
        {
            // R2-M1: don't echo ex.Message — leaks RavenDB-internal strings,
            // index/collection names, etc. Log server-side with correlation ID.
            httpContext.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("SparkLookupReferences")
                ?.LogWarning(ex, "AddLookupReferenceValue failed");
            return Results.Json(new { error = "Operation failed" }, statusCode: 400);
        }
    }
}
