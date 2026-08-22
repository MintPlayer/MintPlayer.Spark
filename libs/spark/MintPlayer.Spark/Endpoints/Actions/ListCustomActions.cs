using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

internal sealed partial class ListCustomActions : IGetEndpoint, IMemberOf<ActionsGroup>
{
    public static string Path => "/{objectTypeId}";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ICustomActionsConfigurationLoader configLoader;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]?.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            // Same shape as a denied type. A known-but-denied type answers 200 with an
            // empty list (the per-action filter below), so a 404 here would have said
            // "this type does not exist" and made type existence probeable.
            return SparkDenial.RefuseJson(httpContext);
        }

        var config = configLoader.GetConfiguration();
        var registeredActions = actionResolver.GetRegisteredActionNames();

        // Get the simple type name (e.g., "Car" from "Fleet.Entities.Car")
        var typeName = entityType.ClrType.Split('.').Last();

        var result = new List<object>();

        foreach (var (actionName, definition) in config)
        {
            // Only include actions that have a C# implementation
            if (!registeredActions.Contains(actionName, StringComparer.OrdinalIgnoreCase))
                continue;

            // Check authorization: resource = "{ActionName}/{EntityTypeName}"
            if (!await permissionService.IsAllowedAsync(actionName, typeName))
                continue;

            result.Add(new
            {
                name = actionName,
                displayName = definition.DisplayName,
                icon = definition.Icon,
                description = definition.Description,
                showedOn = definition.ShowedOn,
                selectionRule = definition.SelectionRule,
                refreshOnCompleted = definition.RefreshOnCompleted,
                confirmationMessageKey = definition.ConfirmationMessageKey,
                offset = definition.Offset,
            });
        }

        // Sort by offset
        result.Sort((a, b) =>
        {
            var aOffset = ((dynamic)a).offset;
            var bOffset = ((dynamic)b).offset;
            return aOffset.CompareTo(bOffset);
        });

        return Results.Json(result);
    }
}
