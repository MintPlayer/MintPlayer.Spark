using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Abstractions.Actions;

/// <summary>One <c>actions.json</c>: a library's compiled one, or the application's file on disk.</summary>
/// <param name="Name">The layer as messages name it: the assembly name, or <see cref="SparkActionLayers.AppLayerName"/>.</param>
/// <param name="Json">The file's text.</param>
/// <param name="IsLibrary">Whether it is a library layer. Only two libraries can conflict (#467, D7).</param>
public sealed record SparkActionsLayer(string Name, string Json, bool IsLibrary);

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

    /// <summary>The stated properties, case-insensitive, in the order they were first stated.</summary>
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
/// library's by assembly name, then the application's. A later layer composes on top <b>per
/// property</b>, and a name no earlier layer has adds an action.
/// </summary>
/// <remarks>
/// <para>
/// Composition runs on the raw JSON objects, before binding to C# classes, because a bound class
/// cannot say "absent": <c>"Edit": null</c> removes the inherited action, and a property set to
/// <c>null</c> resets it to the default (it is no longer stated). Names are case-insensitive.
/// </para>
/// <para>
/// Removal is presentation: rights still decide who may run an action.
/// </para>
/// </remarks>
public static class SparkActionLayers
{
    /// <summary>The application layer's name in messages and in <c>--spark-print-effective-actions</c>.</summary>
    public static string AppLayerName => SparkAppData.Relative("actions.json");

    private const string CoreAssemblyName = "MintPlayer.Spark";

    private static readonly Lazy<IReadOnlyList<SparkActionsLayer>> libraries = new(() => Discover(SparkAssemblies.SparkAware()));

    /// <summary>Every library layer in this process, core first and then by assembly name.</summary>
    public static IReadOnlyList<SparkActionsLayer> Libraries => libraries.Value;

    /// <summary>The library layers <paramref name="assemblies"/> carry, core first and then by assembly name.</summary>
    public static IReadOnlyList<SparkActionsLayer> Discover(IEnumerable<Assembly> assemblies)
    {
        var layers = new List<SparkActionsLayer>();
        foreach (var assembly in assemblies.Distinct())
        {
            SparkActionsAttribute? attribute;
            try { attribute = assembly.GetCustomAttribute<SparkActionsAttribute>(); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException) { continue; }

            if (attribute is not null && assembly.GetName().Name is { } name)
                layers.Add(new SparkActionsLayer(name, attribute.Json, IsLibrary: true));
        }

        return layers
            .OrderBy(l => string.Equals(l.Name, CoreAssemblyName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The layers composed in order.</summary>
    /// <exception cref="InvalidOperationException">A layer is not valid JSON, or not an object of action objects.</exception>
    public static SparkActionsComposition Compose(IEnumerable<SparkActionsLayer> layers)
    {
        var actions = new Dictionary<string, SparkComposedAction>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var conflicts = new List<SparkActionsConflict>();
        var libraryLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in layers)
        {
            if (layer.IsLibrary) libraryLayers.Add(layer.Name);

            foreach (var (name, value) in Entries(layer))
            {
                if (value is null)
                {
                    // "Edit": null — the action is gone from every page, whoever declared it.
                    if (actions.Remove(name))
                        order.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (!actions.TryGetValue(name, out var action))
                {
                    action = new SparkComposedAction(name, layer.Name);
                    actions[name] = action;
                    order.Add(name);
                }

                foreach (var (property, propertyValue) in Properties(layer, name, value))
                {
                    if (propertyValue is null)
                    {
                        // A property set to null is no longer stated: the default applies.
                        action.properties.Remove(property);
                        continue;
                    }

                    if (layer.IsLibrary
                        && action.properties.TryGetValue(property, out var earlier)
                        && libraryLayers.Contains(earlier.Layer)
                        && !string.Equals(earlier.Layer, layer.Name, StringComparison.OrdinalIgnoreCase)
                        && !JsonNode.DeepEquals(earlier.Value, propertyValue))
                    {
                        conflicts.Add(new SparkActionsConflict(action.Name, property, layer.Name, earlier.Layer));
                    }

                    action.properties[property] = new SparkComposedProperty(propertyValue.DeepClone(), layer.Name);
                }
            }
        }

        return new SparkActionsComposition(order.Select(n => actions[n]).ToList(), conflicts);
    }

    private static IEnumerable<(string Name, JsonObject? Value)> Entries(SparkActionsLayer layer)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(layer.Json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{layer.Name} is not valid JSON: {ex.Message}", ex);
        }

        if (root is null) yield break;
        if (root is not JsonObject entries)
            throw new InvalidOperationException(
                $"{layer.Name} must be a JSON object mapping each action name to its definition, or to null to remove it.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries)
        {
            if (IsAnnotation(name, root: true))
                continue;
            if (!seen.Add(name))
                throw new InvalidOperationException($"{layer.Name} declares the action '{name}' twice (names are case-insensitive).");

            yield return value switch
            {
                null => (name, null),
                JsonObject definition => (name, definition),
                _ => throw new InvalidOperationException(
                    $"{layer.Name}: '{name}' must be an object (the action's definition) or null (to remove it)."),
            };
        }
    }

    /// <summary>
    /// Not an action or a property: the file's <c>$schema</c> (root only) and <c>_</c>-prefixed
    /// comments, which the published schema allows everywhere (#264, G-Q12/Q17).
    /// </summary>
    private static bool IsAnnotation(string name, bool root)
        => name.StartsWith('_') || (root && name == "$schema");

    private static IEnumerable<(string Property, JsonNode? Value)> Properties(SparkActionsLayer layer, string action, JsonObject definition)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (property, value) in definition)
        {
            if (IsAnnotation(property, root: false))
                continue;
            if (!seen.Add(property))
                throw new InvalidOperationException($"{layer.Name}: '{action}' states '{property}' twice (names are case-insensitive).");
            yield return (property, value);
        }
    }
}
