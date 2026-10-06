using System;
using System.Collections.Generic;

namespace MintPlayer.Spark.Layering;

/// <summary>One <c>translations.json</c> going into the composition: a library's, or the application's file.</summary>
internal sealed class SparkTranslationsInput
{
    public SparkTranslationsInput(string layer, string json, bool isLibrary, IReadOnlyList<string>? dependsOn = null)
    {
        Layer = layer;
        Json = json;
        IsLibrary = isLibrary;
        DependsOn = dependsOn ?? Array.Empty<string>();
    }

    /// <summary>The layer as messages name it: the library's assembly name, or the application file's path.</summary>
    public string Layer { get; }

    public string Json { get; }

    public bool IsLibrary { get; }

    /// <summary>The library layers this one stacks above: overriding one of those is intended, never a conflict (grill Q3).</summary>
    public IReadOnlyList<string> DependsOn { get; }
}

internal enum SparkTranslationsIssueKind
{
    /// <summary>An object mixes texts with namespaces, or holds a number or a boolean (SPARK_TRANS_002). Refuses the layer.</summary>
    MixedLeafAndNamespace,

    /// <summary>An object states nothing (SPARK_TRANS_003). Skipped.</summary>
    EmptyObject,

    /// <summary>An array (SPARK_TRANS_004). Refuses the layer.</summary>
    ArrayNotAllowed,

    /// <summary>One key reached twice, e.g. <c>"a.b"</c> and <c>{"a":{"b"}}</c> (SPARK_TRANS_006). Refuses the layer.</summary>
    DuplicateKey,
}

/// <summary>Something wrong in one <c>translations.json</c>, at a dotted path.</summary>
internal sealed class SparkTranslationsIssue
{
    public SparkTranslationsIssue(SparkTranslationsIssueKind kind, string path)
    {
        Kind = kind;
        Path = path;
    }

    public SparkTranslationsIssueKind Kind { get; }

    public string Path { get; }

    /// <summary>Only an empty object is harmless; every other issue refuses the layer.</summary>
    public bool Refuses => Kind != SparkTranslationsIssueKind.EmptyObject;

    public string Message => Kind switch
    {
        SparkTranslationsIssueKind.MixedLeafAndNamespace => $"the object at '{Path}' mixes texts with namespaces; a translation holds only strings (language → text), a namespace only objects or null",
        SparkTranslationsIssueKind.EmptyObject => $"the object at '{Path}' is empty",
        SparkTranslationsIssueKind.ArrayNotAllowed => $"the value at '{Path}' is an array",
        _ => $"'{Path}' is stated twice",
    };
}

/// <summary>A <c>translations.json</c> flattened: dotted key → languages, and the prefixes it removes.</summary>
internal sealed class SparkFlatTranslations
{
    internal SparkFlatTranslations(SparkJsonObject keys, IReadOnlyList<string> removals, IReadOnlyList<SparkTranslationsIssue> issues)
    {
        Keys = keys;
        Removals = removals;
        Issues = issues;
    }

    /// <summary>Dotted key → <c>{ language: text }</c>, in file order.</summary>
    public SparkJsonObject Keys { get; }

    /// <summary>The prefixes stated <c>null</c>: each removes that key and every key below it.</summary>
    public IReadOnlyList<string> Removals { get; }

    public IReadOnlyList<SparkTranslationsIssue> Issues { get; }
}

/// <summary>Two libraries translate one key into one language differently. The later library wins.</summary>
internal sealed class SparkTranslationsKeyConflict
{
    public SparkTranslationsKeyConflict(string key, string language, string winnerLayer, string loserLayer)
    {
        Key = key;
        Language = language;
        WinnerLayer = winnerLayer;
        LoserLayer = loserLayer;
    }

    public string Key { get; }

    public string Language { get; }

    public string WinnerLayer { get; }

    public string LoserLayer { get; }
}

/// <summary>The translations composed: key → languages, and the conflicts between unrelated libraries.</summary>
internal sealed class SparkTranslationsResult
{
    internal SparkTranslationsResult(SparkComposition composition, IReadOnlyList<SparkTranslationsKeyConflict> conflicts)
    {
        Composition = composition;
        Conflicts = conflicts;
    }

    /// <summary>The engine's composition: dotted key → <c>{ language: text }</c>, with provenance per text.</summary>
    public SparkComposition Composition { get; }

    public IReadOnlyList<SparkTranslationsKeyConflict> Conflicts { get; }

    /// <summary>Every key with at least one language, its languages in the order they were first stated.</summary>
    public IEnumerable<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>> Entries
    {
        get
        {
            foreach (var entry in Composition.Result.Members)
            {
                var languages = new List<KeyValuePair<string, string>>();
                foreach (var language in ((SparkJsonObject)entry.Value).Members)
                    languages.Add(new KeyValuePair<string, string>(language.Key, ((SparkJsonString)language.Value).Value));
                if (languages.Count > 0)
                    yield return new KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>(entry.Key, languages);
            }
        }
    }
}

/// <summary>
/// Composes <c>translations.json</c> layers (composition D3/D10) with the shared engine and
/// <see cref="SparkKinds.Translations"/>: the run time's composition and the analyzers' are this code.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each layer is flattened first, so <c>"a.b"</c> and <c>{"a":{"b"}}</c> are the same key.</item>
/// <item><c>"ns": null</c> removes <c>ns</c> and every key below it, as far as the layers below state them.</item>
/// <item>A later layer replaces only the languages it states; a new language is appended, so the first stays the fallback.</item>
/// <item>The application's <c>""</c> means "not translated yet" and is ignored; a library's is kept.</item>
/// </list>
/// </remarks>
internal static class SparkTranslationLayers
{
    /// <summary>The tree flattened; issues are collected, never thrown, so a generator can report each one.</summary>
    public static SparkFlatTranslations Flatten(SparkJsonObject root)
    {
        var keys = new SparkJsonObject(StringComparer.Ordinal);
        var removals = new List<string>();
        var issues = new List<SparkTranslationsIssue>();
        Walk(root, "", isRoot: true, keys, removals, issues);
        return new SparkFlatTranslations(keys, removals, issues);
    }

    /// <exception cref="SparkLayerException">The text is not strict JSON, not an object, or has an issue that refuses it.</exception>
    public static SparkFlatTranslations Flatten(string layer, string json)
    {
        var parsed = SparkLayer.Parse(layer, json, isLibrary: false);
        if (parsed.Root is null) return new SparkFlatTranslations(new SparkJsonObject(StringComparer.Ordinal), Array.Empty<string>(), Array.Empty<SparkTranslationsIssue>());

        var flat = Flatten(parsed.Root);
        foreach (var issue in flat.Issues)
            if (issue.Refuses) throw new SparkLayerException($"{layer}: {issue.Message}.");
        return flat;
    }

    /// <summary>The layers composed in order; a library overriding one it depends on is not a conflict (grill Q3).</summary>
    /// <exception cref="SparkLayerException">A layer cannot be read (see <see cref="Flatten(string, string)"/>).</exception>
    public static SparkTranslationsResult Compose(IEnumerable<SparkTranslationsInput> inputs)
    {
        // Every key a lower layer stated, sorted, so a prefix removal finds its keys as one range.
        var stated = new SortedSet<string>(StringComparer.Ordinal);
        var layers = new List<SparkLayer>();
        var dependsOn = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            if (!dependsOn.ContainsKey(input.Layer)) dependsOn[input.Layer] = input.DependsOn;
            var flat = Flatten(input.Layer, input.Json);

            // The removals apply before the layer's own keys, so a layer may remove a namespace and restate part of it.
            if (flat.Removals.Count > 0)
            {
                var removed = new SparkJsonObject(StringComparer.Ordinal);
                foreach (var prefix in flat.Removals)
                {
                    if (stated.Contains(prefix)) removed.Set(prefix, SparkJsonNull.Instance);
                    foreach (var key in stated.GetViewBetween(prefix + ".", prefix + "/"))
                        removed.Set(key, SparkJsonNull.Instance);
                }
                if (removed.Count > 0) layers.Add(new SparkLayer(input.Layer, removed, input.IsLibrary));
            }

            foreach (var key in flat.Keys.Members) stated.Add(key.Key);
            layers.Add(new SparkLayer(input.Layer, flat.Keys, input.IsLibrary));
        }

        var composition = SparkLayers.Compose(layers, SparkKinds.Translations);
        var conflicts = new List<SparkTranslationsKeyConflict>();
        foreach (var conflict in composition.Conflicts)
        {
            if (dependsOn.TryGetValue(conflict.WinnerLayer, out var below) && Contains(below, conflict.LoserLayer)) continue;
            conflicts.Add(new SparkTranslationsKeyConflict(conflict.Path[0], conflict.Path[1], conflict.WinnerLayer, conflict.LoserLayer));
        }
        return new SparkTranslationsResult(composition, conflicts);
    }

    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        foreach (var candidate in names)
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void Walk(SparkJsonObject obj, string path, bool isRoot, SparkJsonObject keys, List<string> removals, List<SparkTranslationsIssue> issues)
    {
        // _-prefixed members are comments, and the root's $schema names the file's JSON schema
        // (#264, G-Q12/Q17): neither is a translation nor a namespace.
        var members = new List<KeyValuePair<string, SparkJsonNode>>();
        foreach (var member in obj.Members)
            if (!SparkLayers.IsAnnotation(member.Key, isRoot)) members.Add(member);

        if (members.Count == 0)
        {
            if (!isRoot) issues.Add(new SparkTranslationsIssue(SparkTranslationsIssueKind.EmptyObject, path));
            return;
        }

        var strings = 0;
        var namespaces = 0;
        var array = false;
        foreach (var member in members)
        {
            switch (member.Value.Kind)
            {
                case SparkJsonKind.String: strings++; break;
                case SparkJsonKind.Object or SparkJsonKind.Null: namespaces++; break;
                case SparkJsonKind.Array:
                    issues.Add(new SparkTranslationsIssue(SparkTranslationsIssueKind.ArrayNotAllowed, SparkLayers.Join(path, member.Key)));
                    array = true;
                    break;
            }
        }
        if (array) return;

        if (strings == members.Count && !isRoot)
        {
            // A translation: language → text.
            var languages = new SparkJsonObject(StringComparer.Ordinal);
            foreach (var member in members)
                languages.Set(member.Key, member.Value);
            if (!keys.TryAdd(path, languages))
                issues.Add(new SparkTranslationsIssue(SparkTranslationsIssueKind.DuplicateKey, path));
            return;
        }

        if (namespaces == members.Count)
        {
            foreach (var member in members)
            {
                var childPath = SparkLayers.Join(path, member.Key);
                if (member.Value is SparkJsonObject child)
                    Walk(child, childPath, isRoot: false, keys, removals, issues);
                else
                    removals.Add(childPath);
            }
            return;
        }

        // Mixed: fail this subtree, keep walking the others so every problem is reported at once.
        issues.Add(new SparkTranslationsIssue(SparkTranslationsIssueKind.MixedLeafAndNamespace, path));
    }
}
