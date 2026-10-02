using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

[MemberOf<ActionsGroup>]
internal sealed partial class ListCustomActions : IPostEndpoint
{
    public static string Path => "/list";

    // ⚠️ EXPLICITLY exempt, not merely unannotated. This is a read; a forged one changes nothing and
    // the attacker cannot see the response. It was exempt by absence until 11.0.0, when
    // SparkAntiforgeryOptions.RequireAntiforgery began defaulting to true and started gating any
    // mutating-verb request under /spark that carries an ambient credential — which swept these in
    // against the decision recorded above. Saying it out loud restores that decision and makes it
    // survive the next default change.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ICustomActionsConfigurationLoader configLoader;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (_, entityType) = await SparkRequestType.ReadAsync<ListCustomActionsRequest>(httpContext, modelLoader);
        if (entityType is null)
        {
            // The empty list, which is exactly what a known-but-denied type gets from the
            // per-action filter below. This is a catalogue endpoint — the shell asks it for every
            // type it renders — so refusing outright would bounce an anonymous visitor to sign-in
            // merely for opening a page.
            //
            // It must be the empty list and NOT SparkDenial: a refusal here answers 404 (or 401)
            // while a denied type answers 200, and the difference is precisely the existence
            // oracle M-3 closes. An earlier revision called SparkDenial with a comment claiming
            // the two shapes matched. They did not.
            return Results.Json(Array.Empty<object>());
        }

        var config = configLoader.GetConfiguration();
        var registeredActions = actionResolver.GetRegisteredActionNames();

        // The type as rights name it: the definition's Name, as everywhere else (a nested class's CLR
        // name ends "Outer+Inner", which names no right).
        var typeName = entityType.Name;

        var result = new List<object>();

        // The built-in actions first (#460, D18), as catalogue entries with the same shape, so a grid
        // enables New and Delete from the selection exactly as it enables a custom action. Governed
        // by the ordinary New/T and Delete/T rights, not by an action right of their own.
        foreach (var defaultName in new[] { SparkDefaultActions.New, SparkDefaultActions.Delete })
        {
            if (!await permissionService.IsAllowedAsync(defaultName, typeName))
                continue;

            result.Add(Describe(defaultName, SparkDefaultActions.Resolve(defaultName, config), isDefault: true));
        }

        foreach (var (actionName, definition) in config)
        {
            // An entry named New or Delete overrides the default above; it is not a custom action.
            if (SparkDefaultActions.IsDefault(actionName))
                continue;

            // Only include actions that have a C# implementation
            if (!registeredActions.Contains(actionName, StringComparer.OrdinalIgnoreCase))
                continue;

            // Check authorization: resource = "{ActionName}/{EntityTypeName}"
            if (!await permissionService.IsAllowedAsync(actionName, typeName))
                continue;

            result.Add(Describe(actionName, definition, isDefault: false));
        }

        // By offset; stable, so equal offsets keep New and Delete ahead of the custom actions.
        var sorted = result
            .Select((item, index) => (item, index))
            .OrderBy(x => ((ActionDescription)x.item).offset)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

        return Results.Json(sorted);
    }

    private static ActionDescription Describe(string name, Models.CustomActionDefinition definition, bool isDefault) => new(
        name,
        definition.DisplayName,
        definition.Icon,
        definition.Description,
        definition.ShowedOn,
        definition.SelectionRule,
        definition.RefreshOnCompleted,
        definition.ConfirmationMessageKey,
        definition.Variant,
        definition.Offset,
        isDefault ? true : null);

    /// <summary>
    /// One listed action. Lower-case members because this is the wire shape, serialized as-is.
    /// <c>isDefault</c> is <c>true</c> for New and Delete and omitted for a custom action.
    /// </summary>
#pragma warning disable IDE1006 // wire names
    private sealed record ActionDescription(
        string name,
        Abstractions.TranslatedString displayName,
        string? icon,
        string? description,
        string showedOn,
        string? selectionRule,
        bool refreshOnCompleted,
        string? confirmationMessageKey,
        string? variant,
        int offset,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        bool? isDefault);
#pragma warning restore IDE1006
}
