using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace MintPlayer.Spark.Layering;

/// <summary>One layer of one kind: a library's compiled payload, or the application's file.</summary>
internal sealed class SparkLayer
{
    public SparkLayer(string name, SparkJsonObject? root, bool isLibrary)
    {
        Name = name;
        Root = root;
        IsLibrary = isLibrary;
    }

    /// <summary>The layer as messages name it: the assembly name, or the application file's path.</summary>
    public string Name { get; }

    /// <summary>What the layer states; <see langword="null"/> when the file is a literal <c>null</c>.</summary>
    public SparkJsonObject? Root { get; }

    /// <summary>Only two libraries can conflict; the application always wins silently.</summary>
    public bool IsLibrary { get; }

    /// <exception cref="SparkLayerException">The text is not JSON, or not an object.</exception>
    public static SparkLayer Parse(string name, string json, bool isLibrary)
    {
        SparkJsonNode root;
        try
        {
            root = SparkJson.Parse(json);
        }
        catch (SparkJsonException ex)
        {
            throw new SparkLayerException($"{name} is not valid JSON: {ex.Message}", ex);
        }

        return root switch
        {
            SparkJsonObject obj => new SparkLayer(name, obj, isLibrary),
            SparkJsonNull => new SparkLayer(name, null, isLibrary),
            _ => throw new SparkLayerException($"{name} must be a JSON object."),
        };
    }
}

/// <summary>A layer cannot be composed at all: invalid JSON, a key stated twice, or a value of the wrong shape.</summary>
internal sealed class SparkLayerException : InvalidOperationException
{
    public SparkLayerException(string message) : base(message) { }

    public SparkLayerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Two libraries state the same leaf differently. The later library wins.</summary>
internal sealed class SparkLayerConflict
{
    public SparkLayerConflict(IReadOnlyList<string> path, string winnerLayer, string loserLayer)
    {
        Path = path;
        WinnerLayer = winnerLayer;
        LoserLayer = loserLayer;
    }

    /// <summary>The concrete path: object keys, and a keyed element as <c>[key]</c>.</summary>
    public IReadOnlyList<string> Path { get; }

    public string WinnerLayer { get; }

    public string LoserLayer { get; }

    public string PathText => SparkLayers.PathText(Path);
}

/// <summary>A layer states something its kind forbids (an immutable id changed, a disallowed <c>null</c>). The statement is ignored.</summary>
internal sealed class SparkLayerError
{
    public SparkLayerError(string path, string layer, string message)
    {
        Path = path;
        Layer = layer;
        Message = message;
    }

    public string Path { get; }

    public string Layer { get; }

    public string Message { get; }

    public override string ToString() => $"{Layer}: {Message}";
}

/// <summary>
/// The rules of one kind of file (composition D3). Callbacks receive the <b>schema path</b>: object
/// keys as written, and an element of a keyed array as <c>[]</c> (<c>persistentObject.attributes.[].showedOn</c>).
/// </summary>
internal sealed class KindSpec
{
    private static readonly Func<IReadOnlyList<string>, bool> Never = _ => false;

    public KindSpec(
        string kind,
        StringComparer keys,
        Func<IReadOnlyList<string>, bool>? isAtomic = null,
        Func<IReadOnlyList<string>, string?>? arrayKey = null,
        Func<IReadOnlyList<string>, bool>? isImmutable = null,
        Func<IReadOnlyList<string>, SparkJsonNode, string?>? shape = null,
        bool nullRemoves = true,
        bool appEmptyStringIsUntranslated = false)
    {
        Kind = kind;
        Keys = keys;
        IsAtomic = isAtomic ?? Never;
        ArrayKey = arrayKey ?? (_ => null);
        IsImmutable = isImmutable ?? Never;
        Shape = shape ?? ((_, _) => null);
        NullRemoves = nullRemoves;
        AppEmptyStringIsUntranslated = appEmptyStringIsUntranslated;
    }

    public string Kind { get; }

    /// <summary>How keys and keyed-array element keys compare (actions: ignoring case; translations: ordinal).</summary>
    public StringComparer Keys { get; }

    /// <summary>A value at this path is replaced whole, never merged deeper.</summary>
    public Func<IReadOnlyList<string>, bool> IsAtomic { get; }

    /// <summary>The element key of an array at this path; <see langword="null"/>: the array is replaced whole.</summary>
    public Func<IReadOnlyList<string>, string?> ArrayKey { get; }

    /// <summary>A value no later layer may change (model ids).</summary>
    public Func<IReadOnlyList<string>, bool> IsImmutable { get; }

    /// <summary>What is wrong with a value at this path, or <see langword="null"/>; a problem refuses the layer.</summary>
    public Func<IReadOnlyList<string>, SparkJsonNode, string?> Shape { get; }

    /// <summary>Whether <c>null</c> (and <c>$remove</c>) may remove what a lower layer states.</summary>
    public bool NullRemoves { get; }

    /// <summary>The application's <c>""</c> means "not translated yet", never "blank" (translations).</summary>
    public bool AppEmptyStringIsUntranslated { get; }
}

/// <summary>The layers composed: the result, where each part came from, and what went wrong.</summary>
internal sealed class SparkComposition
{
    private readonly Dictionary<SparkJsonNode, string> sources;
    private readonly Dictionary<SparkJsonArray, string> keyedArrays;
    private readonly HashSet<SparkJsonObject> mergedObjects;
    private IReadOnlyDictionary<string, string>? provenance;

    internal SparkComposition(
        SparkJsonObject result,
        Dictionary<SparkJsonNode, string> sources,
        Dictionary<SparkJsonArray, string> keyedArrays,
        HashSet<SparkJsonObject> mergedObjects,
        IReadOnlyList<SparkLayerConflict> conflicts,
        IReadOnlyList<SparkLayerError> errors)
    {
        Result = result;
        this.sources = sources;
        this.keyedArrays = keyedArrays;
        this.mergedObjects = mergedObjects;
        Conflicts = conflicts;
        Errors = errors;
    }

    public SparkJsonObject Result { get; }

    public IReadOnlyList<SparkLayerConflict> Conflicts { get; }

    public IReadOnlyList<SparkLayerError> Errors { get; }

    /// <summary>The layer that set a leaf, or that declared a merged object or keyed element; null for the root and keyed arrays.</summary>
    public string? SourceOf(SparkJsonNode node) => sources.TryGetValue(node, out var layer) ? layer : null;

    /// <summary>Every concrete path (<c>Edit.icon</c>, <c>persistentObject.attributes[UserName].showedOn</c>) and its layer, in result order.</summary>
    public IReadOnlyDictionary<string, string> Provenance
    {
        get
        {
            if (provenance is null)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                Walk((path, node, isLeaf) => { if (SourceOf(node) is { } layer) map[path] = layer; });
                provenance = map;
            }
            return provenance;
        }
    }

    /// <summary>
    /// One line per merged object or keyed element (<c>path @layer</c>) and per leaf
    /// (<c>path = json @layer</c>), then one per conflict and error. Deterministic: the golden format.
    /// </summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        Walk((path, node, isLeaf) =>
        {
            builder.Append(path);
            if (isLeaf) builder.Append(" = ").Append(SparkJson.Write(node));
            builder.Append(" @").Append(SourceOf(node)).Append('\n');
        });
        foreach (var conflict in Conflicts)
            builder.Append("conflict ").Append(conflict.PathText).Append(": ").Append(conflict.WinnerLayer).Append(" over ").Append(conflict.LoserLayer).Append('\n');
        foreach (var error in Errors)
            builder.Append("error ").Append(error.Path).Append(" (").Append(error.Layer).Append("): ").Append(error.Message).Append('\n');
        return builder.ToString();
    }

    private void Walk(Action<string, SparkJsonNode, bool> visit) => Walk(Result, "", visit);

    private void Walk(SparkJsonObject obj, string path, Action<string, SparkJsonNode, bool> visit)
    {
        foreach (var member in obj.Members)
        {
            var childPath = SparkLayers.Join(path, member.Key);
            if (member.Value is SparkJsonObject child && mergedObjects.Contains(child))
            {
                visit(childPath, child, false);
                Walk(child, childPath, visit);
            }
            else if (member.Value is SparkJsonArray array && keyedArrays.TryGetValue(array, out var elementKey))
            {
                foreach (var item in array.Items)
                {
                    var element = (SparkJsonObject)item;
                    var elementPath = childPath + "[" + ((SparkJsonString)element[elementKey]!).Value + "]";
                    visit(elementPath, element, false);
                    Walk(element, elementPath, visit);
                }
            }
            else
            {
                visit(childPath, member.Value, true);
            }
        }
    }
}

/// <summary>
/// The one layering engine (composition D2): library defaults below, the application on top, for
/// every kind of <c>App_Data</c> file. Works on the raw JSON tree, before any binding, so "absent"
/// and "removed" stay distinguishable.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Objects merge per property; the first spelling of a key is kept.</item>
/// <item>Arrays the <see cref="KindSpec"/> keys merge per element; <c>{"&lt;key&gt;": "…", "$remove": true}</c> removes one. Other arrays are replaced whole.</item>
/// <item><c>null</c> removes what the layers below state. Removed and later set again, a member is appended.</item>
/// <item><c>$schema</c> (root) and <c>_</c>-prefixed keys are annotations, never data.</item>
/// <item>Two libraries stating a leaf differently is a conflict (the later wins); the application wins silently.</item>
/// </list>
/// </remarks>
internal static class SparkLayers
{
    internal const string RemoveDirective = "$remove";

    /// <exception cref="SparkLayerException">A layer states a key twice, or a value of a shape its kind refuses.</exception>
    public static SparkComposition Compose(IEnumerable<SparkLayer> layers, KindSpec spec)
    {
        var state = new State(spec);
        var result = new SparkJsonObject(spec.Keys);
        foreach (var layer in layers)
        {
            if (layer.IsLibrary) state.LibraryLayers.Add(layer.Name);
            if (layer.Root is not null)
                state.MergeObject(result, layer.Root, new List<string>(), new List<string>(), layer, isRoot: true, elementKey: null);
        }

        return new SparkComposition(result, state.Sources, state.KeyedArrays, state.MergedObjects, state.Conflicts, state.Errors);
    }

    internal static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

    internal static string PathText(IReadOnlyList<string> path)
    {
        var builder = new StringBuilder();
        foreach (var segment in path)
        {
            if (builder.Length > 0 && !segment.StartsWith("[", StringComparison.Ordinal)) builder.Append('.');
            builder.Append(segment);
        }
        return builder.ToString();
    }

    internal static bool IsAnnotation(string key, bool isRoot)
        => key.StartsWith("_", StringComparison.Ordinal) || (isRoot && key == "$schema");

    private sealed class State(KindSpec spec)
    {
        public readonly HashSet<string> LibraryLayers = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<SparkJsonNode, string> Sources = new(ReferenceComparer<SparkJsonNode>.Instance);
        public readonly Dictionary<SparkJsonArray, string> KeyedArrays = new(ReferenceComparer<SparkJsonArray>.Instance);
        public readonly HashSet<SparkJsonObject> MergedObjects = new(ReferenceComparer<SparkJsonObject>.Instance);
        public readonly List<SparkLayerConflict> Conflicts = new();
        public readonly List<SparkLayerError> Errors = new();

        /// <param name="schema">The schema path (keyed elements as <c>[]</c>).</param>
        /// <param name="path">The concrete path (keyed elements as <c>[key]</c>).</param>
        /// <param name="elementKey">Set when <paramref name="source"/> is a keyed-array element: its key and <c>$remove</c> are not data.</param>
        public void MergeObject(SparkJsonObject target, SparkJsonObject source, List<string> schema, List<string> path, SparkLayer layer, bool isRoot, string? elementKey)
        {
            var seen = new HashSet<string>(spec.Keys);
            foreach (var member in source.Members)
            {
                var rawKey = member.Key;
                var value = member.Value;
                if (IsAnnotation(rawKey, isRoot)) continue;
                if (elementKey is not null && rawKey == RemoveDirective) continue;

                var childPath = Append(path, rawKey);
                if (!seen.Add(rawKey))
                    throw new SparkLayerException($"{layer.Name}: '{PathText(childPath)}' is stated twice{CaseNote()}.");

                var exists = target.TryGetMember(rawKey, out var spelling, out var existing);
                var key = exists ? spelling : rawKey;
                if (exists) childPath[childPath.Count - 1] = key;
                var childSchema = Append(schema, key);

                if (spec.Shape(childSchema, value) is { } problem)
                    throw new SparkLayerException($"{layer.Name}: '{PathText(childPath)}' {problem}.");

                // The element's key is how it was matched: a different spelling of it changes nothing.
                if (exists && elementKey is not null && spec.Keys.Equals(rawKey, elementKey)) continue;

                if (value is SparkJsonNull)
                {
                    if (!spec.NullRemoves)
                    {
                        Errors.Add(new SparkLayerError(PathText(childPath), layer.Name, $"'{PathText(childPath)}' cannot be removed with null in a {spec.Kind} file"));
                        continue;
                    }
                    if (exists) target.Remove(key);
                    continue;
                }

                if (spec.AppEmptyStringIsUntranslated && !layer.IsLibrary && value is SparkJsonString { Value.Length: 0 })
                    continue;

                if (exists && spec.IsImmutable(childSchema) && !SparkJsonNode.DeepEquals(existing, value))
                {
                    Errors.Add(new SparkLayerError(PathText(childPath), layer.Name,
                        $"'{PathText(childPath)}' cannot be changed (it is {SparkJson.Write(existing)}, {layer.Name} states {SparkJson.Write(value)})"));
                    continue;
                }

                if (value is SparkJsonObject childSource && !spec.IsAtomic(childSchema))
                {
                    if (!(existing is SparkJsonObject childTarget && MergedObjects.Contains(childTarget)))
                    {
                        childTarget = new SparkJsonObject(spec.Keys);
                        target.Set(key, childTarget);
                        MergedObjects.Add(childTarget);
                        Sources[childTarget] = layer.Name;
                    }
                    MergeObject(childTarget, childSource, childSchema, childPath, layer, isRoot: false, elementKey: null);
                    continue;
                }

                if (value is SparkJsonArray array && spec.ArrayKey(childSchema) is { } arrayKey)
                {
                    if (!(existing is SparkJsonArray targetArray && KeyedArrays.ContainsKey(targetArray)))
                    {
                        targetArray = new SparkJsonArray();
                        target.Set(key, targetArray);
                        KeyedArrays[targetArray] = arrayKey;
                    }
                    MergeKeyedArray(targetArray, array, arrayKey, childSchema, childPath, layer);
                    continue;
                }

                // A leaf, or a value replaced whole.
                if (layer.IsLibrary && exists
                    && Sources.TryGetValue(existing, out var earlier)
                    && LibraryLayers.Contains(earlier)
                    && !string.Equals(earlier, layer.Name, StringComparison.OrdinalIgnoreCase)
                    && !SparkJsonNode.DeepEquals(existing, value))
                {
                    Conflicts.Add(new SparkLayerConflict(childPath, layer.Name, earlier));
                }

                var clone = value.DeepClone();
                target.Set(key, clone);
                Sources[clone] = layer.Name;
            }
        }

        private void MergeKeyedArray(SparkJsonArray target, SparkJsonArray source, string elementKey, List<string> schema, List<string> path, SparkLayer layer)
        {
            // Indexed once per layer, not searched per element (S3 measured a scan at 120 ms).
            var index = new Dictionary<string, SparkJsonObject>(spec.Keys);
            foreach (var item in target.Items)
            {
                var element = (SparkJsonObject)item;
                index[((SparkJsonString)element[elementKey]!).Value] = element;
            }

            var elementSchema = Append(schema, "[]");
            var seen = new HashSet<string>(spec.Keys);
            foreach (var item in source.Items)
            {
                if (item is not SparkJsonObject element || element[elementKey] is not SparkJsonString { Value: var id })
                    throw new SparkLayerException($"{layer.Name}: every element of '{PathText(path)}' must be an object with a string '{elementKey}'.");
                if (!seen.Add(id))
                    throw new SparkLayerException($"{layer.Name}: '{PathText(path)}' states '{id}' twice{CaseNote()}.");

                index.TryGetValue(id, out var existing);
                var elementPath = Append(path, "[" + (existing is null ? id : ((SparkJsonString)existing[elementKey]!).Value) + "]");

                if (element[RemoveDirective] is { } directive)
                {
                    if (directive is not SparkJsonBoolean { Value: true } || CountData(element) != 1)
                        throw new SparkLayerException($"{layer.Name}: '{PathText(elementPath)}' may state only '{elementKey}' next to \"{RemoveDirective}\": true.");
                    if (!spec.NullRemoves)
                    {
                        Errors.Add(new SparkLayerError(PathText(elementPath), layer.Name, $"'{PathText(elementPath)}' cannot be removed in a {spec.Kind} file"));
                        continue;
                    }
                    if (existing is not null)
                    {
                        target.Items.Remove(existing);
                        index.Remove(id);
                    }
                    continue;
                }

                if (existing is null)
                {
                    existing = new SparkJsonObject(spec.Keys);
                    target.Items.Add(existing);
                    index[id] = existing;
                    MergedObjects.Add(existing);
                    Sources[existing] = layer.Name;
                }
                MergeObject(existing, element, elementSchema, elementPath, layer, isRoot: false, elementKey);
            }
        }

        /// <summary>The members that are data: not annotations, not the directive.</summary>
        private static int CountData(SparkJsonObject element)
        {
            var count = 0;
            foreach (var member in element.Members)
                if (!IsAnnotation(member.Key, isRoot: false) && member.Key != RemoveDirective) count++;
            return count;
        }

        private string CaseNote()
            => spec.Keys.Equals("a", "A") ? " (names are case-insensitive)" : "";

        private static List<string> Append(List<string> path, string segment)
            => new List<string>(path) { segment };
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
