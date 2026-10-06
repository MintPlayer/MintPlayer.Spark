using System;

namespace MintPlayer.Spark.Layering;

/// <summary>The <see cref="KindSpec"/> of each layered <c>App_Data</c> kind (composition D3).</summary>
internal static class SparkKinds
{
    /// <summary>
    /// <c>actions.json</c>: action name → definition, names ignoring case. A definition's properties
    /// are replaced whole, an object-valued one too; <c>"Edit": null</c> removes the action.
    /// </summary>
    public static readonly KindSpec Actions = new(
        "actions",
        StringComparer.OrdinalIgnoreCase,
        isAtomic: path => path.Count >= 2,
        shape: (path, value) => path.Count == 1 && value.Kind is not (SparkJsonKind.Object or SparkJsonKind.Null)
            ? "must be an object (the action's definition) or null (to remove it)"
            : null);

    /// <summary>
    /// <c>Model/*.json</c>: attributes, tabs, groups and queries merge by <c>name</c>; an <c>id</c>
    /// can never be changed by a later layer, so an application file for a library type is a delta.
    /// </summary>
    public static readonly KindSpec Model = new(
        "model",
        StringComparer.Ordinal,
        arrayKey: path => path.Count >= 1 && IsNamedCollection(path[path.Count - 1]) ? "name" : null,
        isImmutable: path => path.Count >= 1 && path[path.Count - 1] == "id");

    private static bool IsNamedCollection(string segment)
        => segment is "attributes" or "tabs" or "groups" or "queries";
}
