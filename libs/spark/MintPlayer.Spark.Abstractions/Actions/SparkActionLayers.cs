using System.Reflection;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions.Actions;

/// <summary>One <c>actions.json</c>: a library's compiled one, or the application's file on disk.</summary>
/// <param name="Name">The layer as messages name it: the assembly name, or <see cref="SparkActionLayers.AppLayerName"/>.</param>
/// <param name="Json">The file's text.</param>
/// <param name="IsLibrary">Whether it is a library layer. Only two libraries can conflict (#467, D7).</param>
/// <param name="DependsOn">The library layers this one stacks above; overriding one of those is intended, never a conflict (composition D2, grill Q3).</param>
public sealed record SparkActionsLayer(string Name, string Json, bool IsLibrary, IReadOnlyList<string>? DependsOn = null);

/// <summary>One property of a composed action, and the layer that set it.</summary>
public sealed record SparkComposedProperty(JsonNode Value, string Layer);

/// <summary>An action as the layers compose it, before binding: only the properties some layer states.</summary>
public sealed class SparkComposedAction
{
    internal SparkComposedAction(string name, string layer)
    {
        Name = name;
        DeclaredBy = layer;
    }

    /// <summary>The name as the first layer declaring it spells it.</summary>
    public string Name { get; }

    /// <summary>The layer that declared the action.</summary>
    public string DeclaredBy { get; }

    /// <summary>The stated properties, case-insensitive, in the order they were stated (one reset with null and stated again comes last).</summary>
    public IReadOnlyDictionary<string, SparkComposedProperty> Properties => properties;

    internal readonly Dictionary<string, SparkComposedProperty> properties = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Two libraries state the same property of the same action differently. The later library wins.</summary>
public sealed record SparkActionsConflict(string Action, string Property, string WinnerLayer, string LoserLayer);

/// <summary>The composed catalogue: every action no later layer removed, in declaration order.</summary>
public sealed record SparkActionsComposition(
    IReadOnlyList<SparkComposedAction> Actions,
    IReadOnlyList<SparkActionsConflict> Conflicts);

/// <summary>
/// Composes <c>actions.json</c> in layers (#467, D7/S12): the core library's, then every other
/// library's in layer order (composition D2), then the application's. A later layer composes on top <b>per
/// property</b>, and a name no earlier layer has adds an action.
/// </summary>
/// <remarks>
/// <para>
/// Composition runs on the raw JSON objects, before binding to C# classes, because a bound class
/// cannot say "absent": <c>"Edit": null</c> removes the inherited action, and a property set to
/// <c>null</c> resets it to the default (it is no longer stated). Names are case-insensitive.
/// Each layer is strict JSON: no comments, no trailing commas (<c>_</c>-prefixed keys are the comments).
/// </para>
/// <para>
/// Removal is presentation: rights still decide who may run an action.
/// </para>
/// </remarks>
public static class SparkActionLayers
{
    /// <summary>The application layer's name in messages.</summary>
    public static string AppLayerName => SparkAppData.Relative("actions.json");

    private static readonly Lazy<IReadOnlyList<SparkActionsLayer>> libraries = new(() => From(SparkLayerCatalog.Libraries));

    /// <summary>Every library layer in this process, in layer order (<see cref="SparkLayerCatalog"/>).</summary>
    public static IReadOnlyList<SparkActionsLayer> Libraries => libraries.Value;

    /// <summary>The library layers <paramref name="assemblies"/> carry, in layer order.</summary>
    public static IReadOnlyList<SparkActionsLayer> Discover(IEnumerable<Assembly> assemblies)
        => From(SparkLayerCatalog.Discover(assemblies));

    private static IReadOnlyList<SparkActionsLayer> From(IEnumerable<SparkLibrary> libraries)
        => SparkLayerCatalog.Of(libraries, SparkLayerKinds.Actions)
            .Select(x => new SparkActionsLayer(x.Library.AssemblyName, x.Layer.Json, IsLibrary: true, x.Library.DependsOn))
            .ToList();

    /// <summary>The layers composed in order, by the shared engine (<see cref="SparkLayers"/>, composition D2/D14).</summary>
    /// <exception cref="InvalidOperationException">A layer is not valid JSON, or not an object of action objects.</exception>
    public static SparkActionsComposition Compose(IEnumerable<SparkActionsLayer> layers)
    {
        var list = layers.ToList();
        var composition = SparkLayers.Compose(
            list.Select(l => SparkLayer.Parse(l.Name, l.Json, l.IsLibrary)),
            SparkKinds.Actions);

        var actions = new List<SparkComposedAction>();
        foreach (var entry in composition.Result.Members)
        {
            var definition = (SparkJsonObject)entry.Value;
            var action = new SparkComposedAction(entry.Key, composition.SourceOf(definition)!);
            foreach (var property in definition.Members)
            {
                action.properties[property.Key] = new SparkComposedProperty(
                    JsonNode.Parse(SparkJson.Write(property.Value))!,
                    composition.SourceOf(property.Value)!);
            }
            actions.Add(action);
        }

        // A library overriding a library it depends on means it (grill Q3); only unrelated ones conflict.
        var dependsOn = list.GroupBy(l => l.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DependsOn ?? [], StringComparer.Ordinal);
        var conflicts = composition.Conflicts
            .Where(c => !(dependsOn.TryGetValue(c.WinnerLayer, out var below) && below.Contains(c.LoserLayer, StringComparer.Ordinal)))
            .Select(c => new SparkActionsConflict(c.Path[0], c.Path[1], c.WinnerLayer, c.LoserLayer))
            .ToList();

        return new SparkActionsComposition(actions, conflicts);
    }
}
