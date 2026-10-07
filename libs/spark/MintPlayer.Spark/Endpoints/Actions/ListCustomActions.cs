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
    [Inject] private readonly IActionsCatalogueLoader catalogueLoader;
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

        var catalogue = catalogueLoader.GetCatalogue();
        var registeredActions = actionResolver.GetRegisteredActionNames();

        // The type as rights name it: the definition's Name, as everywhere else (a nested class's CLR
        // name ends "Outer+Inner", which names no right).
        var typeName = entityType.Name;

        var result = new List<ActionDescription>();
        foreach (var action in catalogue.Actions)
        {
            // A custom action needs a C# implementation; New, Edit and Delete are the framework's own
            // (#460 D18, #467 D7) and are governed by the ordinary New/T, Edit/T and Delete/T rights.
            if (!action.IsBuiltIn && !registeredActions.Contains(action.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            // Resource = "{ActionName}/{EntityTypeName}".
            if (!await permissionService.IsAllowedAsync(action.Name, typeName))
                continue;

            result.Add(Describe(action));
        }

        // By offset; stable, so equal offsets keep the catalogue's order (core first).
        var sorted = result
            .Select((item, index) => (item, index))
            .OrderBy(x => x.item.offset)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

        return Results.Json(sorted);
    }

    private static ActionDescription Describe(Models.ActionDefinition definition) => new(
        definition.Name,
        definition.Label,
        definition.Icon,
        definition.Description,
        definition.ShowedOn,
        definition.SelectionRule,
        definition.RefreshOnCompleted,
        definition.Confirmation,
        definition.Variant,
        definition.Offset,
        definition.IsBuiltIn ? true : null,
        definition.RequiresClient);

    /// <summary>
    /// One listed action. Lower-case members because this is the wire shape, serialized as-is. Text is
    /// resolved on the server (#467, D26). <c>isDefault</c> is <c>true</c> for New, Edit and Delete and
    /// omitted for a custom action.
    /// </summary>
#pragma warning disable IDE1006 // wire names
    private sealed record ActionDescription(
        string name,
        Abstractions.TranslatedString label,
        string? icon,
        Abstractions.TranslatedString? description,
        string showedOn,
        string? selectionRule,
        bool refreshOnCompleted,
        Abstractions.TranslatedString? confirmation,
        string? variant,
        int offset,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        bool? isDefault,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? requiresClient);
#pragma warning restore IDE1006
}
