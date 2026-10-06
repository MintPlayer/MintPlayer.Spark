using System;
using System.Collections.Generic;

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
    /// <c>Model/*.json</c>: attributes, tabs, groups and the file's queries merge by <c>name</c>; an
    /// <c>id</c> can never be changed by a later layer, so an application file for a library type is a
    /// delta. <c>persistentObject.queries</c> (the sub-queries: aliases or <c>{ "query": … }</c>, no
    /// name) is replaced whole.
    /// </summary>
    public static readonly KindSpec Model = new(
        "model",
        StringComparer.Ordinal,
        arrayKey: path => IsNamedCollection(path) ? "name" : null,
        isImmutable: path => path.Count >= 1 && path[path.Count - 1] == "id");

    /// <summary>
    /// <c>translations.json</c>, flattened first (<see cref="SparkTranslationLayers"/>): dotted key →
    /// <c>{ language: text }</c>, keys and languages ordinal. A later layer replaces only the languages it
    /// states, and a new one is appended. <c>"key": null</c> removes the key (the flattener expands a
    /// namespace's <c>null</c> into one per key); a language cannot be removed. The application's
    /// <c>""</c> is "not translated yet" and ignored; a library's is kept.
    /// </summary>
    public static readonly KindSpec Translations = new(
        "translations",
        StringComparer.Ordinal,
        shape: (path, value) => path.Count switch
        {
            1 when value.Kind is not (SparkJsonKind.Object or SparkJsonKind.Null) => "must be an object of languages or null (to remove it)",
            2 when value.Kind is not SparkJsonKind.String => "must be a string",
            _ => null,
        },
        appEmptyStringIsUntranslated: true);

    private static bool IsNamedCollection(IReadOnlyList<string> path)
        => path.Count switch
        {
            1 => path[0] == "queries",
            2 => path[0] == "persistentObject" && path[1] is "attributes" or "tabs" or "groups",
            _ => false,
        };
}
