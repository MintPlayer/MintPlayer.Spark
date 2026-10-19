using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Layering;

/// <summary>One <c>Model/*.json</c> going into the composed model: a library's, or one of the application's files.</summary>
internal sealed class SparkModelInput
{
    public SparkModelInput(
        string layer,
        string fileName,
        string json,
        bool isLibrary,
        IReadOnlyList<string>? dependsOn = null,
        string? alias = null,
        string? declaredName = null,
        string? shippedJson = null)
    {
        Layer = layer;
        FileName = fileName;
        Json = json;
        IsLibrary = isLibrary;
        DependsOn = dependsOn ?? Array.Empty<string>();
        Alias = alias;
        DeclaredName = declaredName;
        ShippedJson = shippedJson ?? json;
    }

    /// <summary>The layer as messages name it: the library's assembly name, or the application file's path.</summary>
    public string Layer { get; }

    /// <summary>The file name, e.g. <c>SparkUser.json</c>.</summary>
    public string FileName { get; }

    /// <summary>The text composed: a library's with the application's renames applied (D28).</summary>
    public string Json { get; }

    public bool IsLibrary { get; }

    /// <summary>The library layers this one stacks above: overriding one of those is intended, never a conflict (grill Q3).</summary>
    public IReadOnlyList<string> DependsOn { get; }

    /// <summary>The library's alias; <see langword="null"/> for an application file (and for a library in older callers: the layer name stands in).</summary>
    public string? Alias { get; }

    /// <summary>The name the library's file declares before any rename; <see langword="null"/>: the name it states.</summary>
    public string? DeclaredName { get; }

    /// <summary>The text as the library shipped it, before renames: what provenance hashes cover (D28).</summary>
    public string ShippedJson { get; }
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

    /// <summary>The name the declaring library gives the type, before the application's renames; <see cref="Name"/> for an application type.</summary>
    public string? DeclaredName => Library is { } library ? library.DeclaredName ?? Name : Name;

    /// <summary><c>alias:DeclaredName</c> for a library type (its identity, D28); <see langword="null"/> for an application type.</summary>
    public string? Identity => Library is { } library && DeclaredName is { } declared
        ? SparkRenames.Identity(library.Alias ?? library.Layer, declared)
        : null;
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
    /// libraries stating a field differently, a name two types claim, a delta without or with a stale
    /// <c>$extends</c>. The run time refuses to start on any of them.
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
/// <item>A library type is keyed by its effective <c>persistentObject.name</c> (after the application's
/// renames, D28). A library stacked above the declaring one may state a delta on it; an unrelated library
/// naming it too is a collision (SPARK057): rename one of them.</item>
/// <item>An application file is a <b>delta</b> on a library type only when it says so:
/// <c>"$extends": "alias:DeclaredName"</c>. It states only what differs and never an <c>id</c> (ids are
/// immutable, D5). A file that names a library type without <c>$extends</c> is refused, never silently
/// joined to it (F7): the library's rights follow the name while its row policies follow the CLR type.</item>
/// <item>A delta whose <c>$extends</c> names no shipped type (orphaned) or whose <c>name</c> is not the
/// type's effective name (stale) is refused, and so is an application file without an <c>id</c> (F9): each
/// would otherwise serve the library type without the delta's restrictions.</item>
/// <item>An application file naming no library type is the application's own type; two of them are never merged.</item>
/// </list>
/// </remarks>
internal static class SparkModelLayers
{
    private sealed class LibraryType
    {
        public LibraryType(string name) => Name = name;

        public string Name { get; }

        public List<(SparkModelInput Input, SparkLayer Layer)> Layers { get; } = new();

        public SparkModelInput Declarer => Layers[0].Input;

        public string Identity => SparkRenames.Identity(Declarer.Alias ?? Declarer.Layer, Declarer.DeclaredName ?? Name);

        public string? ClrType => ClrTypeOf(Layers[0].Layer.Root);
    }

    /// <param name="libraries">Library model files, in layer order.</param>
    /// <param name="application">The application's model files, in the order they compose (by file name).</param>
    public static SparkModelComposition Compose(IEnumerable<SparkModelInput> libraries, IEnumerable<SparkModelInput> application)
    {
        var problems = new List<SparkLayerError>();
        var libraryTypes = new List<LibraryType>();
        var byName = new Dictionary<string, LibraryType>(StringComparer.Ordinal);
        var byNameIgnoringCase = new Dictionary<string, LibraryType>(StringComparer.OrdinalIgnoreCase);
        var byIdentity = new Dictionary<string, LibraryType>(StringComparer.Ordinal);

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

            if (byNameIgnoringCase.TryGetValue(name, out var type))
            {
                var declarer = type.Declarer;
                if (string.Equals(type.Name, name, StringComparison.Ordinal)
                    && (string.Equals(declarer.Layer, input.Layer, StringComparison.Ordinal) || input.DependsOn.Contains(declarer.Layer, StringComparer.Ordinal)))
                {
                    // A library stacked above the declaring one: a delta on its type (grill Q3).
                    type.Layers.Add((input, layer));
                    continue;
                }

                problems.Add(new SparkLayerError(input.FileName, input.Layer,
                    $"libraries '{declarer.Layer}' and '{input.Layer}' both ship a persistent-object type named '{name}'" +
                    (string.Equals(type.Name, name, StringComparison.Ordinal) ? "" : $" ('{type.Name}' and '{name}': names compare ignoring case)") +
                    $" (SPARK057). Rename one of them in the application: [assembly: PersistentObjectName(\"{input.Alias ?? input.Layer}\", \"{input.DeclaredName ?? name}\", \"<NewName>\")]."));
                continue;
            }

            type = new LibraryType(name);
            type.Layers.Add((input, layer));
            byName[name] = type;
            byNameIgnoringCase[name] = type;
            byIdentity[type.Identity] = type;
            libraryTypes.Add(type);
        }

        var byClrType = libraryTypes
            .Where(t => t.ClrType is not null)
            .GroupBy(t => t.ClrType!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

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
            if (layer is not null && layer.Root is { } root && root[SparkLayers.ExtendsDirective] is { } directive)
            {
                if (Extended(input, root, directive, name, byIdentity, problems) is { } extended)
                    extended.Layers.Add((input, layer));
                else
                    appOwned.Add(Refused(input));
                continue;
            }

            if (layer is not null && name is not null)
            {
                if (byNameIgnoringCase.TryGetValue(name, out var named))
                {
                    // F7: joining by name made an application class a delta on the library type, under the
                    // library's id and rights while the library's row policies (keyed by CLR type) no longer applied.
                    problems.Add(new SparkLayerError(input.FileName, input.Layer,
                        $"{input.Layer} names '{name}', a type {named.Declarer.Layer} ships ('{named.Identity}'), without \"$extends\". " +
                        $"To change the library's type, state \"$extends\": \"{named.Identity}\" and only what differs (never an id). " +
                        $"To keep a type of your own, give it another name, or rename the library's: [assembly: PersistentObjectName(\"{named.Declarer.Alias ?? named.Declarer.Layer}\", \"{named.Declarer.DeclaredName ?? named.Name}\", \"<NewName>\")] (D28)."));
                    appOwned.Add(Refused(input));
                    continue;
                }

                if (ClrTypeOf(layer.Root) is { } clrType && byClrType.TryGetValue(clrType, out var owner))
                {
                    problems.Add(new SparkLayerError(input.FileName, input.Layer,
                        $"{input.Layer} maps '{clrType}', the CLR type of {owner.Declarer.Layer}'s '{owner.Name}', under the name '{name}'. " +
                        $"A file for a library type is a delta on it: state \"$extends\": \"{owner.Identity}\" and the name '{owner.Name}'; " +
                        "to give it another name, use [assembly: PersistentObjectName<T>(\"<NewName>\")] (D28)."));
                    appOwned.Add(Refused(input));
                    continue;
                }

                if (!HasId(layer.Root))
                {
                    // F9: an id-less file used to be skipped by the loader with a console line, and a library type
                    // it once changed was served without its restrictions.
                    problems.Add(new SparkLayerError(input.FileName, input.Layer,
                        $"{input.Layer} states no persistentObject.id, so it is neither a type of the application's own nor a delta " +
                        "(a delta states \"$extends\"). If it changed a library type, that type is no longer shipped under this name: " +
                        "remove the file, or anchor it with \"$extends\": \"alias:Name\" (run --spark-synchronize-model)."));
                    appOwned.Add(Refused(input));
                    continue;
                }
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
        foreach (var type in libraryTypes)
        {
            var name = type.Name;
            var layers = type.Layers;
            SparkComposition composition;
            try
            {
                composition = SparkLayers.Compose(layers.Select(l => l.Layer), SparkKinds.Model);
            }
            catch (SparkLayerException ex)
            {
                problems.Add(new SparkLayerError(name, layers[layers.Count - 1].Input.Layer, ex.Message));
                continue;
            }

            problems.AddRange(composition.Errors);
            var dependsOn = layers.Where(l => l.Input.IsLibrary)
                .GroupBy(l => l.Input.Layer, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Input.DependsOn, StringComparer.Ordinal);
            foreach (var conflict in composition.Conflicts)
            {
                if (dependsOn.TryGetValue(conflict.WinnerLayer, out var below) && below.Contains(conflict.LoserLayer, StringComparer.Ordinal))
                    continue;
                problems.Add(new SparkLayerError(conflict.PathText, conflict.WinnerLayer,
                    $"libraries '{conflict.LoserLayer}' and '{conflict.WinnerLayer}' both state '{name}.{conflict.PathText}', differently"));
            }

            types.Add(new SparkModelEntry(
                name,
                layers[0].Input.FileName,
                SparkJson.Write(composition.Result, indented: true),
                composition,
                layers.Select(l => l.Input).ToList()));
        }

        types.AddRange(appOwned);
        return new SparkModelComposition(types, problems);
    }

    /// <summary>
    /// An application file refused as a problem, still listed as written (no name, no composition): the
    /// run time refuses the model anyway, and a lenient reader (the hash gate) must still see the file,
    /// or a planted one would hash as absent.
    /// </summary>
    private static SparkModelEntry Refused(SparkModelInput input)
        => new(null, input.FileName, input.Json, null, new[] { input });

    /// <summary>The library type an application file's <c>$extends</c> anchors on, or <see langword="null"/> after reporting why it cannot.</summary>
    private static LibraryType? Extended(
        SparkModelInput input,
        SparkJsonObject root,
        SparkJsonNode directive,
        string? name,
        IReadOnlyDictionary<string, LibraryType> byIdentity,
        List<SparkLayerError> problems)
    {
        if (directive is not SparkJsonString { Value: var target } || target.IndexOf(':') <= 0)
        {
            problems.Add(new SparkLayerError(SparkLayers.ExtendsDirective, input.Layer,
                $"{input.Layer}: \"{SparkLayers.ExtendsDirective}\" must be \"alias:DeclaredName\", the library's alias and the name it declares the type under (e.g. \"authorization:SparkUser\")."));
            return null;
        }

        if (!byIdentity.TryGetValue(target, out var extended))
        {
            problems.Add(new SparkLayerError(SparkLayers.ExtendsDirective, input.Layer,
                $"{input.Layer} extends '{target}', which no referenced library ships (an orphaned delta, F9). " +
                "Its restrictions would be lost, so the model is refused: remove the file, or restore the library reference."));
            return null;
        }

        if (!string.Equals(name, extended.Name, StringComparison.Ordinal))
        {
            problems.Add(new SparkLayerError("persistentObject.name", input.Layer,
                $"{input.Layer} extends '{target}' under the name '{name}', but that type is named '{extended.Name}' (a stale delta). " +
                "Run --spark-synchronize-model, which moves the file to the type's name."));
            return null;
        }

        if (ClrTypeOf(root) is { } clrType && !string.Equals(clrType, extended.ClrType, StringComparison.Ordinal))
        {
            problems.Add(new SparkLayerError("persistentObject.clrType", input.Layer,
                $"{input.Layer} extends '{target}' but maps '{clrType}', not the library's '{extended.ClrType}'. A delta cannot change a type's CLR class; " +
                "an application class of its own is its own type, under another name."));
            return null;
        }

        return extended;
    }

    /// <summary>
    /// What an application file must state so that <paramref name="library"/> composes into
    /// <paramref name="desired"/>: only the members that differ (D6). Never an <c>id</c> the library
    /// states, and never a removal: a member the library has and <paramref name="desired"/> lacks is
    /// left to the library, as the synchronizer never deletes (#253). <see langword="null"/> when
    /// nothing differs.
    /// </summary>
    /// <param name="extends">The library type's <c>alias:DeclaredName</c>, written as <c>$extends</c> (D28); none when omitted.</param>
    /// <remarks>The result keys <c>$extends</c> and <c>persistentObject.name</c> first, so it composes onto the library type.</remarks>
    public static SparkJsonObject? Delta(SparkJsonObject library, SparkJsonObject desired, string? extends = null)
    {
        var delta = SparkLayers.Delta(library, desired, SparkKinds.Model);
        if (delta.Count == 0) return null;

        var result = new SparkJsonObject(SparkKinds.Model.Keys);
        if (extends is not null) result.Set(SparkLayers.ExtendsDirective, new SparkJsonString(extends));
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

    /// <summary><c>persistentObject.clrType</c>, or <see langword="null"/>.</summary>
    public static string? ClrTypeOf(SparkJsonObject? root)
        => root?["persistentObject"] is SparkJsonObject po && po["clrType"] is SparkJsonString { Value.Length: > 0 } clrType
            ? clrType.Value
            : null;

    /// <summary><c>$extends</c>, or <see langword="null"/>.</summary>
    public static string? ExtendsOf(SparkJsonObject? root)
        => root?[SparkLayers.ExtendsDirective] is SparkJsonString { Value.Length: > 0 } extends ? extends.Value : null;

    private static bool HasId(SparkJsonObject? root)
        => root?["persistentObject"] is SparkJsonObject po && po["id"] is SparkJsonString { Value.Length: > 0 };
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
