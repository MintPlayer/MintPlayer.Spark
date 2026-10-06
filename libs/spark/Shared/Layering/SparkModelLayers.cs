using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Layering;

/// <summary>One <c>Model/*.json</c> going into the composed model: a library's, or one of the application's files.</summary>
internal sealed class SparkModelInput
{
    public SparkModelInput(string layer, string fileName, string json, bool isLibrary, IReadOnlyList<string>? dependsOn = null)
    {
        Layer = layer;
        FileName = fileName;
        Json = json;
        IsLibrary = isLibrary;
        DependsOn = dependsOn ?? Array.Empty<string>();
    }

    /// <summary>The layer as messages name it: the library's assembly name, or the application file's path.</summary>
    public string Layer { get; }

    /// <summary>The file name, e.g. <c>SparkUser.json</c>.</summary>
    public string FileName { get; }

    public string Json { get; }

    public bool IsLibrary { get; }

    /// <summary>The library layers this one stacks above: overriding one of those is intended, never a conflict (grill Q3).</summary>
    public IReadOnlyList<string> DependsOn { get; }
}

/// <summary>One persistent-object type of the composed model.</summary>
internal sealed class SparkModelEntry
{
    internal SparkModelEntry(string? name, string fileName, string json, SparkComposition? composition, IReadOnlyList<SparkModelInput> inputs)
    {
        Name = name;
        FileName = fileName;
        Json = json;
        Composition = composition;
        Inputs = inputs;
    }

    /// <summary><c>persistentObject.name</c>; <see langword="null"/> for an application file that names none or does not parse.</summary>
    public string? Name { get; }

    /// <summary>The library's file name for a library type, the application file's name otherwise.</summary>
    public string FileName { get; }

    /// <summary>
    /// The composed file. An application file that cannot be composed (not strict JSON, no
    /// <c>persistentObject.name</c>) is passed through as written, so its reader reports it as before.
    /// </summary>
    public string Json { get; }

    /// <summary>The composition, with provenance per leaf; <see langword="null"/> for a file passed through.</summary>
    public SparkComposition? Composition { get; }

    /// <summary>Every layer that states this type, in order.</summary>
    public IReadOnlyList<SparkModelInput> Inputs { get; }

    /// <summary>The library that declares the type; <see langword="null"/> when the application owns it.</summary>
    public SparkModelInput? Library => Inputs.FirstOrDefault(i => i.IsLibrary);
}

/// <summary>The composed model, and every statement that could not be honoured.</summary>
internal sealed class SparkModelComposition
{
    internal SparkModelComposition(IReadOnlyList<SparkModelEntry> types, IReadOnlyList<SparkLayerError> problems)
    {
        Types = types;
        Problems = problems;
    }

    public IReadOnlyList<SparkModelEntry> Types { get; }

    /// <summary>
    /// A library layer that cannot be read, an application file changing an <c>id</c>, two unrelated
    /// libraries stating a field differently. The run time refuses to start on any of them.
    /// </summary>
    public IReadOnlyList<SparkLayerError> Problems { get; }
}

/// <summary>
/// The composed model (composition D3, D6): every library's <c>Model/*.json</c> in layer order, then the
/// application's files, composed per type by <see cref="SparkKinds.Model"/>. One source for the run time
/// and the generators (D14), so <c>PersistentObjectIds</c> and the model the server loads cannot differ.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A type is keyed by <c>persistentObject.name</c>. An application file naming a library type is a
/// <b>delta</b> on it: it states only what differs, never an <c>id</c> (ids are immutable, D5).</item>
/// <item>An application file naming no library type is the application's own type, as before; two of
/// them are never merged.</item>
/// </list>
/// </remarks>
internal static class SparkModelLayers
{
    /// <param name="libraries">Library model files, in layer order.</param>
    /// <param name="application">The application's model files, in the order they compose (by file name).</param>
    public static SparkModelComposition Compose(IEnumerable<SparkModelInput> libraries, IEnumerable<SparkModelInput> application)
    {
        var problems = new List<SparkLayerError>();
        var libraryTypes = new List<(string Name, List<(SparkModelInput Input, SparkLayer Layer)> Layers)>();
        var byName = new Dictionary<string, List<(SparkModelInput, SparkLayer)>>(StringComparer.Ordinal);

        foreach (var input in libraries)
        {
            SparkLayer layer;
            try
            {
                layer = SparkLayer.Parse(input.Layer, input.Json, isLibrary: true);
            }
            catch (SparkLayerException ex)
            {
                problems.Add(new SparkLayerError(input.FileName, input.Layer, ex.Message));
                continue;
            }

            if (NameOf(layer.Root) is not { } name)
            {
                problems.Add(new SparkLayerError(input.FileName, input.Layer, $"{input.Layer}: Model/{input.FileName} states no persistentObject.name"));
                continue;
            }

            if (!byName.TryGetValue(name, out var layers))
            {
                layers = new List<(SparkModelInput, SparkLayer)>();
                byName[name] = layers;
                libraryTypes.Add((name, layers));
            }
            layers.Add((input, layer));
        }

        var appOwned = new List<SparkModelEntry>();
        foreach (var input in application)
        {
            SparkLayer? layer = null;
            try
            {
                layer = SparkLayer.Parse(input.Layer, input.Json, isLibrary: false);
            }
            catch (SparkLayerException)
            {
                // Its own reader reports it, in its own words, as it did before layering.
            }

            var name = NameOf(layer?.Root);
            if (layer is not null && name is not null && byName.TryGetValue(name, out var layers))
            {
                layers.Add((input, layer));
                continue;
            }

            SparkComposition? composition = null;
            var json = input.Json;
            if (layer is not null && name is not null)
            {
                try
                {
                    composition = SparkLayers.Compose(new[] { layer }, SparkKinds.Model);
                    json = SparkJson.Write(composition.Result, indented: true);
                }
                catch (SparkLayerException)
                {
                    composition = null;
                }
            }
            appOwned.Add(new SparkModelEntry(composition is null ? null : name, input.FileName, json, composition, new[] { input }));
        }

        var types = new List<SparkModelEntry>();
        foreach (var (name, layers) in libraryTypes)
        {
            SparkComposition composition;
            try
            {
                composition = SparkLayers.Compose(layers.Select(l => l.Item2), SparkKinds.Model);
            }
            catch (SparkLayerException ex)
            {
                problems.Add(new SparkLayerError(name, layers[layers.Count - 1].Item1.Layer, ex.Message));
                continue;
            }

            problems.AddRange(composition.Errors);
            var dependsOn = layers.Where(l => l.Item1.IsLibrary)
                .GroupBy(l => l.Item1.Layer, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Item1.DependsOn, StringComparer.Ordinal);
            foreach (var conflict in composition.Conflicts)
            {
                if (dependsOn.TryGetValue(conflict.WinnerLayer, out var below) && below.Contains(conflict.LoserLayer, StringComparer.Ordinal))
                    continue;
                problems.Add(new SparkLayerError(conflict.PathText, conflict.WinnerLayer,
                    $"libraries '{conflict.LoserLayer}' and '{conflict.WinnerLayer}' both state '{name}.{conflict.PathText}', differently"));
            }

            types.Add(new SparkModelEntry(
                name,
                layers[0].Item1.FileName,
                SparkJson.Write(composition.Result, indented: true),
                composition,
                layers.Select(l => l.Item1).ToList()));
        }

        types.AddRange(appOwned);
        return new SparkModelComposition(types, problems);
    }

    /// <summary>
    /// What an application file must state so that <paramref name="library"/> composes into
    /// <paramref name="desired"/>: only the members that differ (D6). Never an <c>id</c> the library
    /// states, and never a removal: a member the library has and <paramref name="desired"/> lacks is
    /// left to the library, as the synchronizer never deletes (#253). <see langword="null"/> when
    /// nothing differs.
    /// </summary>
    /// <remarks>The result keys <c>persistentObject.name</c> first, so it composes onto the library type.</remarks>
    public static SparkJsonObject? Delta(SparkJsonObject library, SparkJsonObject desired)
    {
        var delta = SparkLayers.Delta(library, desired, SparkKinds.Model);
        if (delta.Count == 0) return null;

        var result = new SparkJsonObject(SparkKinds.Model.Keys);
        var persistentObject = new SparkJsonObject(SparkKinds.Model.Keys);
        persistentObject.Set("name", new SparkJsonString(NameOf(library) ?? NameOf(desired) ?? string.Empty));
        if (delta["persistentObject"] is SparkJsonObject changed)
            foreach (var member in changed.Members)
                persistentObject.Set(member.Key, member.Value);
        result.Set("persistentObject", persistentObject);
        foreach (var member in delta.Members)
            if (member.Key != "persistentObject") result.Set(member.Key, member.Value);
        return result;
    }

    /// <summary><c>persistentObject.name</c>, or <see langword="null"/>.</summary>
    public static string? NameOf(SparkJsonObject? root)
        => root?["persistentObject"] is SparkJsonObject po && po["name"] is SparkJsonString { Value.Length: > 0 } name
            ? name.Value
            : null;
}

/// <summary>A model element whose shipped <c>id</c> is missing or not the one D5 derives.</summary>
internal sealed class SparkModelIdProblem
{
    public SparkModelIdProblem(string path, string seed, string? actual, string expected)
    {
        Path = path;
        Seed = seed;
        Actual = actual;
        Expected = expected;
    }

    /// <summary>Where the id belongs, e.g. <c>persistentObject.attributes[UserName].id</c>.</summary>
    public string Path { get; }

    /// <summary>The UUIDv5 name it is derived from, e.g. <c>authorization:SparkUser.attributes.UserName</c>.</summary>
    public string Seed { get; }

    public string? Actual { get; }

    public string Expected { get; }
}

/// <summary>
/// Ids of library-shipped model elements (composition D5): UUIDv5 over the library alias, the type and,
/// for a member, its collection and name. They are written into the shipped file and verified by the
/// library's generator (SPARK045), never computed at run time, so a rename shows up as a diagnostic
/// instead of silently changing an id on the wire.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Type: <c>{alias}:{Type}</c> → <c>persistentObject.id</c>.</item>
/// <item>Member: <c>{alias}:{Type}.{attributes|tabs|groups}.{Name}</c> → that element's <c>id</c>;
/// <c>{alias}:{Type}.queries.{Name}</c> for the file's queries.</item>
/// </list>
/// <c>tools/stamp-library-model-ids.mjs</c> writes them (<c>npm run stamp:library-model-ids</c>).
/// </remarks>
internal static class SparkModelIds
{
    /// <summary>
    /// The UUIDv5 namespace of every library model id. ⚠️ Fixed forever: changing it changes every id
    /// every library ships, in every application.
    /// </summary>
    public static readonly Guid Namespace = new("036a66ee-1790-46de-9f0e-4e1d856750c9");

    /// <summary>The id <paramref name="seed"/> derives in <see cref="Namespace"/>.</summary>
    public static Guid For(string seed) => Create(Namespace, seed);

    /// <summary>RFC 4122 §4.3 version 5 (SHA-1) UUID of <paramref name="name"/> in <paramref name="namespaceId"/>.</summary>
    public static Guid Create(Guid namespaceId, string name)
    {
        var ns = namespaceId.ToByteArray();
        ToNetworkOrder(ns);
        var text = Encoding.UTF8.GetBytes(name);
        var input = new byte[ns.Length + text.Length];
        Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
        Buffer.BlockCopy(text, 0, input, ns.Length, text.Length);

        byte[] hash;
        using (var sha1 = SHA1.Create())
            hash = sha1.ComputeHash(input);

        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        ToNetworkOrder(bytes);
        return new Guid(bytes);
    }

    /// <summary>Every id in <paramref name="root"/> that is missing or wrong for <paramref name="alias"/>.</summary>
    public static IReadOnlyList<SparkModelIdProblem> Verify(string alias, SparkJsonObject root)
    {
        var problems = new List<SparkModelIdProblem>();
        if (SparkModelLayers.NameOf(root) is not { } type) return problems;

        var po = (SparkJsonObject)root["persistentObject"]!;
        Check(problems, po, "persistentObject.id", $"{alias}:{type}");
        foreach (var collection in new[] { "attributes", "tabs", "groups" })
            CheckElements(problems, po[collection], $"persistentObject.{collection}", $"{alias}:{type}.{collection}");
        CheckElements(problems, root["queries"], "queries", $"{alias}:{type}.queries");
        return problems;
    }

    private static void CheckElements(List<SparkModelIdProblem> problems, SparkJsonNode? array, string path, string seed)
    {
        if (array is not SparkJsonArray items) return;
        foreach (var item in items.Items)
        {
            if (item is SparkJsonObject element && element["name"] is SparkJsonString { Value: var name })
                Check(problems, element, $"{path}[{name}].id", $"{seed}.{name}");
        }
    }

    private static void Check(List<SparkModelIdProblem> problems, SparkJsonObject element, string path, string seed)
    {
        var expected = For(seed).ToString("D");
        var actual = element["id"] is SparkJsonString { Value: var value } ? value : null;
        if (!(actual is not null && Guid.TryParse(actual, out var parsed) && parsed.ToString("D") == expected))
            problems.Add(new SparkModelIdProblem(path, seed, actual, expected));
    }

    /// <summary>Swaps a <see cref="Guid"/>'s first three fields between .NET's little-endian layout and RFC 4122 order.</summary>
    private static void ToNetworkOrder(byte[] guid)
    {
        Swap(guid, 0, 3);
        Swap(guid, 1, 2);
        Swap(guid, 4, 5);
        Swap(guid, 6, 7);
    }

    private static void Swap(byte[] bytes, int a, int b)
    {
        var t = bytes[a];
        bytes[a] = bytes[b];
        bytes[b] = t;
    }
}
