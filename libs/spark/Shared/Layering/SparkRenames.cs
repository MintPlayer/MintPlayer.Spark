using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.Layering;

/// <summary>
/// One <c>[assembly: PersistentObjectName]</c> the application states (D28, X9): a library type by its
/// CLR class or by <c>alias</c> + declared name, or one of the application's own CLR types.
/// </summary>
internal sealed class SparkRename
{
    public SparkRename(string? library, string? declaredName, string? clrType, string name)
    {
        Library = library;
        DeclaredName = declaredName;
        ClrType = clrType;
        Name = name;
    }

    /// <summary>The library's alias (the non-generic form); <see langword="null"/> for the generic form.</summary>
    public string? Library { get; }

    /// <summary>The name the library declares (the non-generic form).</summary>
    public string? DeclaredName { get; }

    /// <summary>The CLR class's full name (the generic form).</summary>
    public string? ClrType { get; }

    /// <summary>The name the type gets.</summary>
    public string Name { get; }

    /// <summary>The attribute as the developer wrote it, for messages.</summary>
    public string Text => ClrType is not null
        ? $"[assembly: PersistentObjectName<{ClrType}>(\"{Name}\")]"
        : $"[assembly: PersistentObjectName(\"{Library}\", \"{DeclaredName}\", \"{Name}\")]";
}

/// <summary>A library as the renames see it: its alias, what it stacks above, and its model files.</summary>
internal sealed class SparkRenameLibrary
{
    public SparkRenameLibrary(string alias, string assembly, IReadOnlyList<string> dependsOn, IReadOnlyList<string> modelLayers)
    {
        Alias = alias;
        Assembly = assembly;
        DependsOn = dependsOn;
        ModelLayers = modelLayers;
    }

    public string Alias { get; }

    public string Assembly { get; }

    /// <summary>The layered assemblies it stacks above.</summary>
    public IReadOnlyList<string> DependsOn { get; }

    /// <summary>The text of each <c>Model/*.json</c> it ships, as shipped.</summary>
    public IReadOnlyList<string> ModelLayers { get; }
}

/// <summary>A rename that cannot be honoured (SPARK061 at build time; startup refuses).</summary>
internal sealed class SparkRenameProblem
{
    public SparkRenameProblem(SparkRename rename, string message)
    {
        Rename = rename;
        Message = message;
    }

    public SparkRename Rename { get; }

    public string Message { get; }

    public override string ToString() => $"{Rename.Text}: {Message}";
}

/// <summary>A library type the application renamed: <c>alias:DeclaredName</c> → <see cref="Name"/>.</summary>
internal sealed class SparkRenamedType
{
    public SparkRenamedType(string alias, string assembly, string declaredName, string? clrType, string name)
    {
        Alias = alias;
        Assembly = assembly;
        DeclaredName = declaredName;
        ClrType = clrType;
        Name = name;
    }

    public string Alias { get; }

    public string Assembly { get; }

    public string DeclaredName { get; }

    public string? ClrType { get; }

    public string Name { get; }

    /// <summary><c>alias:DeclaredName</c>: the type's identity, the seed of its id and the target of <c>$extends</c>.</summary>
    public string Identity => SparkRenames.Identity(Alias, DeclaredName);
}

/// <summary>What the application's renames mean against the libraries it composes.</summary>
internal sealed class SparkRenamePlan
{
    private readonly IReadOnlyDictionary<string, SparkRenameLibrary> librariesByAssembly;

    internal SparkRenamePlan(
        IReadOnlyList<SparkRenamedType> libraryTypes,
        IReadOnlyDictionary<string, string> applicationTypes,
        IReadOnlyList<SparkRenameProblem> problems,
        IReadOnlyDictionary<string, SparkRenameLibrary> librariesByAssembly)
    {
        LibraryTypes = libraryTypes;
        ApplicationTypes = applicationTypes;
        Problems = problems;
        this.librariesByAssembly = librariesByAssembly;
    }

    /// <summary>The library types renamed.</summary>
    public IReadOnlyList<SparkRenamedType> LibraryTypes { get; }

    /// <summary>The application's own CLR types named by the generic form: CLR full name → name.</summary>
    public IReadOnlyDictionary<string, string> ApplicationTypes { get; }

    public IReadOnlyList<SparkRenameProblem> Problems { get; }

    public bool IsEmpty => LibraryTypes.Count == 0;

    /// <summary>
    /// Declared name → new name for the layers of <paramref name="assembly"/>: its own types, and the
    /// types of the libraries it stacks above (a library may state a delta on, or grant rights to, a
    /// type it depends on). Empty when nothing it can name was renamed.
    /// </summary>
    public IReadOnlyDictionary<string, string> MapFor(string assembly)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (LibraryTypes.Count == 0) return map;

        var visible = new HashSet<string>(StringComparer.Ordinal) { assembly };
        if (librariesByAssembly.TryGetValue(assembly, out var library))
            foreach (var below in library.DependsOn) visible.Add(below);

        foreach (var type in LibraryTypes)
            if (visible.Contains(type.Assembly) && !map.ContainsKey(type.DeclaredName))
                map[type.DeclaredName] = type.Name;
        return map;
    }
}

/// <summary>
/// Persistent-object renames (D28, X9), applied once where the library layers load: at run time in
/// <c>SparkLayerCatalog</c>, at build time in <c>LibraryLayersReader</c>. One function for both (D14),
/// so the build sees the names startup serves.
/// </summary>
/// <remarks>
/// <para>
/// A rename rewrites every layer of the declaring library (and of each library stacked above it) that
/// names the type: <c>persistentObject.name</c> and the file's <c>queries[].entityType</c> in the model;
/// the target segment of each right (<c>Verb/Old</c>, <c>Verb/Old/Attr</c>), so the library's grants
/// follow the type instead of silently covering a new type of the application's that takes the old
/// name; and the <c>model.Old.</c> translation keys. Program units are keyed by id, and
/// <c>actions.json</c> and <c>moderation.json</c> name no type: nothing to rewrite there.
/// </para>
/// <para>
/// The text as shipped is kept beside the rewritten one: provenance hashes are over what the library
/// ships, so a rename is reported as a rename (the names map), never as "the library changed".
/// </para>
/// </remarks>
internal static class SparkRenames
{
    /// <summary><c>alias:DeclaredName</c>.</summary>
    public static string Identity(string alias, string declaredName) => alias + ":" + declaredName;

    /// <summary>
    /// A name a type may have: a C# identifier (letters, digits, <c>_</c>; not starting with a digit). It
    /// is spliced into rights (<c>Verb/Name/Attr</c>), translation keys (<c>model.Name.label</c>), slots
    /// (<c>alias:slot</c>) and generated constants, so <c>/</c>, <c>.</c> and <c>:</c> can never appear.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (name is null || name.Length == 0 || !(name[0] == '_' || char.IsLetter(name[0]))) return false;
        for (var i = 1; i < name.Length; i++)
            if (!(name[i] == '_' || char.IsLetterOrDigit(name[i]))) return false;
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="renames"/> against the types <paramref name="libraries"/> declare
    /// (in layer order). A generic rename whose CLR type no library ships names an application type.
    /// </summary>
    public static SparkRenamePlan Plan(IReadOnlyList<SparkRenameLibrary> libraries, IEnumerable<SparkRename> renames)
    {
        var problems = new List<SparkRenameProblem>();
        var declarations = Declarations(libraries);
        var byIdentity = declarations.ToDictionary(d => Identity(d.Alias, d.DeclaredName), d => d, StringComparer.Ordinal);
        var aliases = new HashSet<string>(libraries.Select(l => l.Alias), StringComparer.Ordinal);

        var renamed = new List<SparkRenamedType>();
        var applicationTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stated = renames.ToList();

        foreach (var rename in stated)
        {
            if (!IsValidName(rename.Name))
            {
                problems.Add(new SparkRenameProblem(rename,
                    $"'{rename.Name}' is not a valid persistent-object name. Use letters, digits and '_' (not starting with a digit): " +
                    "the name becomes part of rights ('Verb/Name/Attribute'), translation keys ('model.Name.label') and generated constants."));
                continue;
            }

            Declaration? target;
            if (rename.ClrType is not null)
            {
                target = declarations.FirstOrDefault(d => string.Equals(d.ClrType, rename.ClrType, StringComparison.Ordinal));
                if (target is null)
                {
                    // The application's own CLR type: named here instead of by its class name.
                    if (!seen.Add("clr:" + rename.ClrType))
                    {
                        problems.Add(new SparkRenameProblem(rename, $"'{rename.ClrType}' is renamed more than once. State one name per type."));
                        continue;
                    }
                    applicationTypes[rename.ClrType] = rename.Name;
                    continue;
                }
            }
            else
            {
                if (rename.Library is null || !aliases.Contains(rename.Library))
                {
                    problems.Add(new SparkRenameProblem(rename,
                        $"no referenced library has the alias '{rename.Library}'. The non-generic form renames a library type by its library's alias " +
                        "and declared name; an application renames its own JSON-only type by changing 'name' in its model file."));
                    continue;
                }
                if (rename.DeclaredName is null || !byIdentity.TryGetValue(Identity(rename.Library, rename.DeclaredName), out target))
                {
                    problems.Add(new SparkRenameProblem(rename,
                        $"library '{rename.Library}' ships no persistent-object type named '{rename.DeclaredName}'."));
                    continue;
                }
            }

            var identity = Identity(target.Alias, target.DeclaredName);
            if (!seen.Add(identity) || (target.ClrType is not null && !seen.Add("clr:" + target.ClrType)))
            {
                problems.Add(new SparkRenameProblem(rename, $"'{identity}' is renamed more than once. State one name per type."));
                continue;
            }
            if (string.Equals(target.DeclaredName, rename.Name, StringComparison.Ordinal)) continue;
            renamed.Add(new SparkRenamedType(target.Alias, target.Assembly, target.DeclaredName, target.ClrType, rename.Name));
        }

        // A new name must be free, ignoring case: rights compare ignoring case, and so do the name lookups.
        var effective = new List<(string Name, string Owner, bool Renamed)>();
        foreach (var declaration in declarations)
        {
            var rename = renamed.FirstOrDefault(r => r.Alias == declaration.Alias && r.DeclaredName == declaration.DeclaredName);
            effective.Add((rename?.Name ?? declaration.DeclaredName, Identity(declaration.Alias, declaration.DeclaredName), rename is not null));
        }
        foreach (var pair in applicationTypes)
            effective.Add((pair.Value, pair.Key, true));

        foreach (var group in effective.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2 || !members.Any(m => m.Renamed)) continue;
            foreach (var member in members.Where(m => m.Renamed))
            {
                var others = string.Join(", ", members.Where(m => !string.Equals(m.Owner, member.Owner, StringComparison.Ordinal)).Select(m => $"'{m.Owner}'"));
                var source = stated.First(r => string.Equals(r.Name, member.Name, StringComparison.Ordinal));
                problems.Add(new SparkRenameProblem(source,
                    $"'{member.Name}' is already the name of {others} (names compare ignoring case). Choose a name no other type has."));
            }
        }

        var failed = new HashSet<string>(problems.Select(p => p.Rename.Name), StringComparer.Ordinal);
        return new SparkRenamePlan(
            renamed.Where(r => !failed.Contains(r.Name)).ToList(),
            applicationTypes.Where(p => !failed.Contains(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            problems,
            libraries.GroupBy(l => l.Assembly, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
    }

    /// <summary>
    /// <paramref name="json"/> (one layer of <paramref name="kind"/>) with every name in
    /// <paramref name="map"/> (declared → new) replaced; the text unchanged when it names none of
    /// them or cannot be read (its own reader reports that).
    /// </summary>
    public static string Rewrite(string kind, string json, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return json;
        SparkJsonNode parsed;
        try { parsed = SparkJson.Parse(json); }
        catch (SparkJsonException) { return json; }
        if (parsed is not SparkJsonObject root) return json;

        var changed = kind switch
        {
            SparkLayerKinds.Model => RewriteModel(root, map),
            SparkLayerKinds.Security => RewriteSecurity(root, map),
            SparkLayerKinds.Translations => RewriteTranslations(ref root, map),
            _ => false,
        };
        return changed ? SparkJson.Write(root, indented: true) : json;
    }

    private static bool RewriteModel(SparkJsonObject root, IReadOnlyDictionary<string, string> map)
    {
        var changed = false;
        if (root["persistentObject"] is SparkJsonObject po
            && po["name"] is SparkJsonString { Value: var name }
            && map.TryGetValue(name, out var renamed))
        {
            po.Set("name", new SparkJsonString(renamed));
            changed = true;
        }

        if (root["queries"] is SparkJsonArray queries)
        {
            foreach (var query in queries.Items.OfType<SparkJsonObject>())
            {
                if (query["entityType"] is SparkJsonString { Value: var entityType } && map.TryGetValue(entityType, out var target))
                {
                    query.Set("entityType", new SparkJsonString(target));
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>The target segment of every right: <c>Verb/Old</c> → <c>Verb/New</c>, <c>Verb/Old/Attr</c> → <c>Verb/New/Attr</c>; names ignore case, as rights do.</summary>
    private static bool RewriteSecurity(SparkJsonObject root, IReadOnlyDictionary<string, string> map)
    {
        var ignoringCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map) ignoringCase[pair.Key] = pair.Value;

        var changed = false;
        if (root.Get("rights", StringComparer.OrdinalIgnoreCase) is not SparkJsonArray rights) return false;
        foreach (var right in rights.Items.OfType<SparkJsonObject>())
        {
            var key = right.Members.Select(m => m.Key).FirstOrDefault(k => string.Equals(k, "resource", StringComparison.OrdinalIgnoreCase));
            if (key is null || right[key] is not SparkJsonString { Value: var resource }) continue;

            var segments = resource.Split('/');
            if (segments.Length < 2 || !ignoringCase.TryGetValue(segments[1], out var renamed)) continue;
            segments[1] = renamed;
            right.Set(key, new SparkJsonString(string.Join("/", segments)));
            changed = true;
        }
        return changed;
    }

    /// <summary>The <c>model.Old.</c> keys, on the flattened form (<c>"a.b"</c> and <c>{"a":{"b"}}</c> are one key).</summary>
    private static bool RewriteTranslations(ref SparkJsonObject root, IReadOnlyDictionary<string, string> map)
    {
        var flat = SparkTranslationLayers.Flatten(root);
        if (flat.Issues.Any(i => i.Refuses)) return false;

        string Renamed(string key)
        {
            if (!key.StartsWith("model.", StringComparison.Ordinal)) return key;
            var rest = key.Substring("model.".Length);
            var dot = rest.IndexOf('.');
            var type = dot < 0 ? rest : rest.Substring(0, dot);
            return map.TryGetValue(type, out var renamed) ? "model." + renamed + (dot < 0 ? "" : rest.Substring(dot)) : key;
        }

        var any = flat.Removals.Any(r => Renamed(r) != r) || flat.Keys.Members.Any(m => Renamed(m.Key) != m.Key);
        if (!any) return false;

        var rewritten = new SparkJsonObject(StringComparer.Ordinal);
        foreach (var member in root.Members)
            if (SparkLayers.IsAnnotation(member.Key, isRoot: true)) rewritten.Set(member.Key, member.Value);
        foreach (var removal in flat.Removals)
            rewritten.Set(Renamed(removal), SparkJsonNull.Instance);
        foreach (var member in flat.Keys.Members)
            rewritten.Set(Renamed(member.Key), member.Value);
        root = rewritten;
        return true;
    }

    private sealed class Declaration
    {
        public Declaration(string alias, string assembly, string declaredName, string? clrType)
        {
            Alias = alias;
            Assembly = assembly;
            DeclaredName = declaredName;
            ClrType = clrType;
        }

        public string Alias { get; }
        public string Assembly { get; }
        public string DeclaredName { get; }
        public string? ClrType { get; }
    }

    /// <summary>The types each library declares: a file naming a type a library below it declares is a delta, not a declaration.</summary>
    private static List<Declaration> Declarations(IReadOnlyList<SparkRenameLibrary> libraries)
    {
        var result = new List<Declaration>();
        var declaredBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in libraries)
        {
            foreach (var json in library.ModelLayers)
            {
                SparkJsonNode root;
                try { root = SparkJson.Parse(json); }
                catch (SparkJsonException) { continue; }
                if (root is not SparkJsonObject file || SparkModelLayers.NameOf(file) is not { } name) continue;

                if (declaredBy.TryGetValue(name, out var owner) && (owner == library.Assembly || library.DependsOn.Contains(owner, StringComparer.Ordinal)))
                    continue;
                declaredBy[name] = library.Assembly;
                var clrType = file["persistentObject"] is SparkJsonObject po && po["clrType"] is SparkJsonString { Value.Length: > 0 } clr ? clr.Value : null;
                result.Add(new Declaration(library.Alias, library.Assembly, name, clrType));
            }
        }
        return result;
    }
}
