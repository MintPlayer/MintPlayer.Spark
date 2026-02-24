using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class ListCustomActions
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ICustomActionsConfigurationLoader configLoader;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
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

        await httpContext.Response.WriteAsJsonAsync(result);
    }
}
