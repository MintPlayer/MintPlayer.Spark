using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[MemberOf<LookupReferencesGroup>]
internal sealed partial class UpdateLookupReferenceValue : IPutEndpoint<LookupReferenceValueDto>
{
    public static string Path => "/{name}/{key}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [RouteParam] public string Name { get; set; } = "";
    [RouteParam] public string Key { get; set; } = "";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly ILogger<UpdateLookupReferenceValue> logger;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => LookupReferenceBodies.BindFailedAsync(context, permissionService, failure);

    public override async Task<IResult> HandleAsync(LookupReferenceValueDto value, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;

        try
        {
            await permissionService.EnsureAuthorizedAsync("Edit", "LookupReferences"); // R2-H4

            var result = await lookupReferenceService.UpdateValueAsync(Name, Key, value);
            return Results.Json(result);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
        catch (InvalidOperationException ex)
        {
            // R2-M1: don't leak Raven-internal strings — log server-side.
            logger.LogWarning(ex, "UpdateLookupReferenceValue failed");
            return Results.Json(new { error = "Operation failed" }, statusCode: 400);
        }
    }
}
