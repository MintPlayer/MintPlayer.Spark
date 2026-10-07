using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.Layering;

/// <summary>What a <see cref="SparkSecurityProblem"/> is about; each kind is one diagnostic.</summary>
internal enum SparkSecurityProblemKind
{
    /// <summary>A library layer breaks a guard rail (D4): a deny, an important right, a resource it does not own, a group by id, an application-only member. SPARK047.</summary>
    GuardRail,

    /// <summary>A group token, slot or binding does not resolve to a declared group. SPARK048.</summary>
    Unresolved,

    /// <summary>The application states something about library grants that cannot mean what it says: it edits one, removes one no library ships, or opts out of or binds a library that is not referenced. SPARK049.</summary>
    Composition,
}

/// <summary>One reason the composed <c>security.json</c> is refused. The run time refuses startup with all of them; the analyzers report each.</summary>
internal sealed class SparkSecurityProblem
{
    public SparkSecurityProblem(SparkSecurityProblemKind kind, string layer, string message)
    {
        Kind = kind;
        Layer = layer;
        Message = message;
    }

    public SparkSecurityProblemKind Kind { get; }

    /// <summary>The library alias, or the application file's name.</summary>
    public string Layer { get; }

    public string Message { get; }

    public override string ToString() => $"{Layer}: {Message}";
}

/// <summary>A referenced library as the security composer sees it: its alias, its <c>security.json</c> (if it ships one), and the types its model layers declare.</summary>
internal sealed class SparkSecurityLibrary
{
    public SparkSecurityLibrary(string alias, string? json, IEnumerable<string> ownTypes)
    {
        Alias = alias;
        Json = json;
        OwnTypes = new HashSet<string>(ownTypes, StringComparer.OrdinalIgnoreCase);
    }

    public string Alias { get; }

    /// <summary>The library's <c>security.json</c>; <see langword="null"/> when it ships none (its alias still names slots and opt-outs).</summary>
    public string? Json { get; }

    /// <summary>The persistent-object and query names and aliases its model layers declare (<see cref="SparkSecurityLayers.ModelTargets"/>).</summary>
    public ISet<string> OwnTypes { get; }
}

/// <summary>One composed grant, before its group resolves.</summary>
internal sealed class SparkSecurityGrant
{
    public SparkSecurityGrant(string key, string resource, string group, bool isDenied, bool isImportant, string? library)
    {
        Key = key;
        Resource = resource;
        Group = group;
        IsDenied = isDenied;
        IsImportant = isImportant;
        Library = library;
    }

    /// <summary>The application's key as written, or <c>alias:key</c> for a library grant.</summary>
    public string Key { get; }

    public string Resource { get; }

    /// <summary>The <c>groupId</c> as written: a group id, <c>@anonymous</c>, <c>@authenticated</c> or a slot (<c>alias:slot</c>).</summary>
    public string Group { get; }

    public bool IsDenied { get; }

    public bool IsImportant { get; }

    /// <summary>The library alias that ships it; <see langword="null"/> for the application's own.</summary>
    public string? Library { get; }

    /// <summary>The group ids <see cref="Group"/> resolves to; empty until resolved, and for an inert grant.</summary>
    public IReadOnlyList<string> GroupIds { get; internal set; } = [];
}

/// <summary>The composed <c>security.json</c>: the effective grants, the opted-out ones, and every problem.</summary>
internal sealed class SparkSecurityLayersResult
{
    internal SparkSecurityLayersResult(
        List<SparkSecurityGrant> grants,
        List<SparkSecurityGrant> inert,
        List<SparkSecurityProblem> problems,
        List<KeyValuePair<string, string>> groups,
        Dictionary<string, string>? wellKnown,
        Dictionary<string, List<string>>? bindings,
        Dictionary<string, bool>? libraries)
    {
        Grants = grants;
        Inert = inert;
        Problems = problems;
        Groups = groups;
        WellKnown = wellKnown;
        Bindings = bindings;
        Libraries = libraries;
    }

    /// <summary>Every effective grant, libraries first in layer order, then the application's, each once.</summary>
    public IReadOnlyList<SparkSecurityGrant> Grants { get; }

    /// <summary>The grants of libraries the application opted out of (<c>"libraries": { alias: false }</c>), shown in the posture table and never applied.</summary>
    public IReadOnlyList<SparkSecurityGrant> Inert { get; }

    public IReadOnlyList<SparkSecurityProblem> Problems { get; }

    /// <summary>The application's <c>groups</c>, id → name, in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Groups { get; }

    public IReadOnlyDictionary<string, string>? WellKnown { get; }

    public IReadOnlyDictionary<string, List<string>>? Bindings { get; }

    public IReadOnlyDictionary<string, bool>? Libraries { get; }
}

/// <summary>
/// Composes <c>security.json</c> in layers (composition D4): the rights each referenced library ships,
/// in layer order, then the application's file. One source for the run time and the analyzers (D14).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>rights</c> is a keyed set (<see cref="SparkKinds.Security"/>). A library writes its keys bare and
/// they are namespaced by its alias (<c>passkeys-read</c> → <c>authorization:passkeys-read</c>); the
/// application keys its own rights with anything that has no <c>:</c> (its existing ids).</item>
/// <item>The application never edits a library grant. It removes one with
/// <c>{"key": "authorization:passkeys-read", "$remove": true}</c> and adds its own, or switches off a
/// whole library with <c>"libraries": { "authorization": false }</c>.</item>
/// <item>Guard rails on library layers: grants only (no <c>isDenied</c>, no <c>isImportant</c>), only on
/// targets the library ships (its model types and queries, and the <c>reservedTargets</c> it declares),
/// only to tokens (<c>@anonymous</c>, <c>@authenticated</c>) or its own slots (<c>alias:slot</c>).</item>
/// <item>Tokens resolve through <c>wellKnown</c>, slots through the application's
/// <c>"bindings": { "moderation:moderators": ["Moderators"] }</c> (group ids or names). Anything that does
/// not resolve is a problem, never a grant to nobody.</item>
/// </list>
/// </remarks>
internal static class SparkSecurityLayers
{
    public const string AnonymousToken = "@anonymous";
    public const string AuthenticatedToken = "@authenticated";

    private const string RightsMember = "rights";
    private const string KeyMember = "key";
    private const string GroupMember = "groupId";
    private const string ResourceMember = "resource";
    private const string ReservedTargetsMember = "reservedTargets";

    /// <summary>
    /// <paramref name="libraries"/> (in layer order) composed with the application's file. Without an
    /// application (<paramref name="appJson"/> <see langword="null"/>, a library's own build) only the
    /// guard rails are checked: nothing can resolve.
    /// </summary>
    /// <param name="modelTypes">The composed model's type names, so a reserved target cannot claim one; <see langword="null"/>: not checked.</param>
    /// <exception cref="SparkLayerException">A layer is not strict JSON, or a right is not an object with a string <c>key</c>.</exception>
    public static SparkSecurityLayersResult Compose(IReadOnlyList<SparkSecurityLibrary> libraries, string? appJson, string appName, ICollection<string>? modelTypes = null)
    {
        var problems = new List<SparkSecurityProblem>();
        var app = appJson is null ? null : SparkLayer.Parse(appName, appJson, isLibrary: false);
        var appRoot = app?.Root;
        var aliases = new HashSet<string>(libraries.Select(l => l.Alias), StringComparer.Ordinal);

        var groups = ReadGroups(appRoot, appName, problems);
        var wellKnown = ReadStrings(appRoot, "wellKnown", appName, problems);
        var bindings = ReadBindings(appRoot, appName, aliases, problems);
        var optOut = ReadLibraries(appRoot, appName, aliases, problems);

        var layers = new List<SparkLayer>();
        var libraryLayers = new HashSet<string>(StringComparer.Ordinal);
        var inert = new List<SparkSecurityGrant>();
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            if (library.Json is null) continue;
            var root = SparkLayer.Parse(library.Alias, library.Json, isLibrary: true).Root;
            if (root is null) continue;

            var rights = CheckLibrary(library, root, modelTypes, reservedBy, problems);
            foreach (SparkJsonObject element in rights.Items)
                shipped.Add(((SparkJsonString)Get(element, KeyMember)!).Value);

            if (optOut != null && optOut.TryGetValue(library.Alias, out var on) && !on)
            {
                foreach (SparkJsonObject element in rights.Items)
                    inert.Add(ToGrant(element, library.Alias, library.Alias, problems));
                continue;
            }

            var layer = new SparkJsonObject(SparkKinds.Security.Keys);
            layer.Set(RightsMember, rights);
            layers.Add(new SparkLayer(library.Alias, layer, isLibrary: true));
            libraryLayers.Add(library.Alias);
        }

        if (app is not null)
        {
            if (appRoot is not null) CheckApplication(appRoot, appName, aliases, shipped, problems);
            layers.Add(app);
        }

        var composition = SparkLayers.Compose(layers, SparkKinds.Security);
        foreach (var error in composition.Errors)
            problems.Add(new SparkSecurityProblem(SparkSecurityProblemKind.Composition, error.Layer, error.Message));

        var grants = new List<SparkSecurityGrant>();
        if (composition.Result[RightsMember] is SparkJsonArray composed)
        {
            foreach (SparkJsonObject element in composed.Items)
            {
                var source = composition.SourceOf(element);
                var library = source is not null && libraryLayers.Contains(source) ? source : null;
                grants.Add(ToGrant(element, library, library ?? appName, problems));
            }
        }

        if (app is not null)
        {
            if (bindings is not null)
            {
                foreach (var binding in bindings)
                    foreach (var value in binding.Value)
                        if (GroupReference(value, groups) is null)
                            problems.Add(new SparkSecurityProblem(SparkSecurityProblemKind.Unresolved, appName,
                                $"binds '{binding.Key}' to '{value}', which is neither the id nor the name of a group the 'groups' block declares."));
            }

            foreach (var grant in grants)
            {
                if (grant.Group.Length == 0) continue; // ToGrant reported it.
                var ids = Resolve(grant.Group, groups, wellKnown, bindings, out var problem);
                if (ids is null)
                    problems.Add(new SparkSecurityProblem(SparkSecurityProblemKind.Unresolved, grant.Library ?? appName, $"right '{grant.Key}' ({grant.Resource}) {problem}"));
                else
                    grant.GroupIds = ids;
            }
        }

        return new SparkSecurityLayersResult(grants, inert, problems, groups, wellKnown, bindings, optOut);
    }

    /// <summary>
    /// The group ids <paramref name="group"/> stands for: a group id as itself, <c>@anonymous</c> and
    /// <c>@authenticated</c> through <c>wellKnown</c>, a slot through <c>bindings</c> (each bound value a
    /// group id or name). <see langword="null"/> with <paramref name="problem"/> (a sentence's predicate)
    /// when it does not resolve.
    /// </summary>
    public static List<string>? Resolve(
        string group,
        IEnumerable<KeyValuePair<string, string>> groups,
        IReadOnlyDictionary<string, string>? wellKnown,
        IReadOnlyDictionary<string, List<string>>? bindings,
        out string? problem)
    {
        problem = null;
        if (string.Equals(group, AnonymousToken, StringComparison.OrdinalIgnoreCase)
            || string.Equals(group, AuthenticatedToken, StringComparison.OrdinalIgnoreCase))
        {
            var role = group.Substring(1).ToLowerInvariant();
            string? id = null;
            if (wellKnown is not null)
                foreach (var entry in wellKnown)
                    if (string.Equals(entry.Key, role, StringComparison.OrdinalIgnoreCase)) id = entry.Value;
            if (id is null)
            {
                problem = $"names '{group}', and security.json declares no \"wellKnown\": {{ \"{role}\": \"<group id>\" }} for it to resolve to.";
                return null;
            }
            return [id];
        }

        if (group.StartsWith("@", StringComparison.Ordinal))
        {
            problem = $"names '{group}', which is not a token. The tokens are {AnonymousToken} and {AuthenticatedToken}.";
            return null;
        }

        if (IsSlot(group, out _))
        {
            List<string>? bound = null;
            if (bindings is not null)
                foreach (var entry in bindings)
                    if (string.Equals(entry.Key, group, StringComparison.OrdinalIgnoreCase)) bound = entry.Value;
            if (bound is null || bound.Count == 0)
            {
                problem = $"names the slot '{group}', which security.json does not bind. Add \"bindings\": {{ \"{group}\": [\"<group id or name>\"] }}.";
                return null;
            }

            var ids = new List<string>();
            foreach (var value in bound)
            {
                var id = GroupReference(value, groups);
                if (id is null)
                {
                    problem = $"names the slot '{group}', bound to '{value}', which is neither the id nor the name of a declared group.";
                    return null;
                }
                if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
            }
            return ids;
        }

        if (!Guid.TryParse(group, out _))
        {
            problem = $"names group '{group}', which is neither a group id nor a token ({AnonymousToken}, {AuthenticatedToken}, <alias>:<slot>).";
            return null;
        }
        return [group];
    }

    /// <summary>The persistent-object and query names and aliases <paramref name="modelLayers"/> declare: the targets a library owns.</summary>
    public static IEnumerable<string> ModelTargets(IEnumerable<string> modelLayers)
    {
        var targets = new List<string>();
        foreach (var json in modelLayers)
        {
            SparkJsonNode root;
            try { root = SparkJson.Parse(json); }
            catch (SparkJsonException) { continue; } // The model composer reports it.

            if (root is not SparkJsonObject file) continue;
            if (Get(file, "persistentObject") is SparkJsonObject po)
                AddNames(po, targets);
            if (Get(file, "queries") is SparkJsonArray queries)
                foreach (var query in queries.Items.OfType<SparkJsonObject>())
                    AddNames(query, targets);
        }
        return targets;

        static void AddNames(SparkJsonObject element, List<string> into)
        {
            if (Get(element, "name") is SparkJsonString { Value.Length: > 0 } name) into.Add(name.Value);
            if (Get(element, "alias") is SparkJsonString { Value.Length: > 0 } alias) into.Add(alias.Value);
        }
    }

    /// <summary>Whether <paramref name="value"/> is <c>alias:slot</c>, both lower-kebab.</summary>
    public static bool IsSlot(string value, out string alias)
    {
        alias = string.Empty;
        var colon = value.IndexOf(':');
        if (colon <= 0 || colon != value.LastIndexOf(':')) return false;
        alias = value.Substring(0, colon);
        return IsKebab(alias) && IsKebab(value.Substring(colon + 1));
    }

    /// <summary>Checks a library layer against the guard rails and returns its rights, keyed <c>alias:key</c>.</summary>
    private static SparkJsonArray CheckLibrary(SparkSecurityLibrary library, SparkJsonObject root, ICollection<string>? modelTypes, Dictionary<string, string> reservedBy, List<SparkSecurityProblem> problems)
    {
        var alias = library.Alias;
        var owned = new HashSet<string>(library.OwnTypes, StringComparer.OrdinalIgnoreCase);
        SparkJsonNode? rightsNode = null;
        foreach (var member in root.Members)
        {
            if (SparkLayers.IsAnnotation(member.Key, isRoot: true)) continue;
            if (Is(member.Key, RightsMember))
            {
                rightsNode = member.Value;
                continue;
            }
            if (Is(member.Key, ReservedTargetsMember))
            {
                if (member.Value is not SparkJsonArray targets || targets.Items.Any(t => t is not SparkJsonString))
                    throw new SparkLayerException($"{alias}: '{ReservedTargetsMember}' must be an array of names.");
                foreach (SparkJsonString target in targets.Items)
                {
                    if (modelTypes is not null && modelTypes.Contains(target.Value))
                    {
                        problems.Add(GuardRail(alias, $"declares the reserved target '{target.Value}', which is a model type. A reserved target names a surface that has no model file, so it cannot claim a type."));
                        continue;
                    }
                    if (reservedBy.TryGetValue(target.Value, out var other) && other != alias)
                    {
                        problems.Add(GuardRail(alias, $"declares the reserved target '{target.Value}', which '{other}' already declares."));
                        continue;
                    }
                    reservedBy[target.Value] = alias;
                    owned.Add(target.Value);
                }
                continue;
            }

            problems.Add(GuardRail(alias, $"states '{member.Key}', which only the application's security.json may state. A library ships '{RightsMember}' and '{ReservedTargetsMember}'."));
        }

        var result = new SparkJsonArray();
        if (rightsNode is null or SparkJsonNull) return result;
        if (rightsNode is not SparkJsonArray rights)
            throw new SparkLayerException($"{alias}: '{RightsMember}' must be an array of rights.");

        foreach (var item in rights.Items)
        {
            if (item is not SparkJsonObject element || Get(element, KeyMember) is not SparkJsonString { Value: var key })
                throw new SparkLayerException($"{alias}: every element of '{RightsMember}' must be an object with a string '{KeyMember}'.");

            if (key.IndexOf(':') >= 0)
            {
                problems.Add(GuardRail(alias, $"keys a right '{key}'. A library writes its keys without the alias; the alias is added ('{alias}:{key.Substring(key.IndexOf(':') + 1)}')."));
                continue;
            }
            if (Get(element, SparkLayers.RemoveDirective) is not null)
            {
                problems.Add(GuardRail(alias, $"removes '{key}'. A library only grants; removing a grant is the application's decision."));
                continue;
            }

            var resource = (Get(element, ResourceMember) as SparkJsonString)?.Value ?? string.Empty;
            if (Get(element, "isDenied") is SparkJsonBoolean { Value: true })
                problems.Add(GuardRail(alias, $"denies '{resource}' (right '{key}'). A library may only grant; a denial is the application's decision."));
            if (Get(element, "isImportant") is SparkJsonBoolean { Value: true })
                problems.Add(GuardRail(alias, $"marks '{resource}' important (right '{key}'). A library grants ordinary rights only; the important tier is the application's."));

            var slash = resource.IndexOf('/');
            if (slash > 0)
            {
                var rest = resource.Substring(slash + 1);
                var target = rest.IndexOf('/') is var next and >= 0 ? rest.Substring(0, next) : rest;
                if (!owned.Contains(target))
                {
                    var own = owned.Count == 0 ? "it ships none" : string.Join(", ", owned.OrderBy(t => t, StringComparer.Ordinal));
                    problems.Add(GuardRail(alias, $"grants '{resource}' (right '{key}') on '{target}', which it does not ship. A library grants only on its own types, queries and reserved targets ({own})."));
                }
            }

            var group = (Get(element, GroupMember) as SparkJsonString)?.Value ?? string.Empty;
            var isToken = string.Equals(group, AnonymousToken, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(group, AuthenticatedToken, StringComparison.OrdinalIgnoreCase);
            if (!isToken && !(IsSlot(group, out var slotAlias) && slotAlias == alias))
            {
                problems.Add(GuardRail(alias, $"grants '{resource}' (right '{key}') to '{group}'. A library names groups only by token ({AnonymousToken}, {AuthenticatedToken}) or by one of its own slots ('{alias}:<slot>'), which the application binds; never by id."));
            }

            var namespaced = (SparkJsonObject)element.DeepClone();
            namespaced.Set(KeyMemberSpelling(element), new SparkJsonString(alias + ":" + key));
            result.Items.Add(namespaced);
        }

        return result;
    }

    /// <summary>What the application states about library grants: it removes one by key, never edits it.</summary>
    private static void CheckApplication(SparkJsonObject root, string appName, HashSet<string> aliases, HashSet<string> shipped, List<SparkSecurityProblem> problems)
    {
        if (Get(root, RightsMember) is not SparkJsonArray rights) return;
        foreach (var item in rights.Items)
        {
            if (item is not SparkJsonObject element || Get(element, KeyMember) is not SparkJsonString { Value: var key }) continue; // The engine refuses it.
            var colon = key.IndexOf(':');
            if (colon < 0) continue;

            var prefix = key.Substring(0, colon);
            var removes = Get(element, SparkLayers.RemoveDirective) is SparkJsonBoolean { Value: true };
            if (!aliases.Contains(prefix))
            {
                problems.Add(Composition(appName, $"states the right '{key}'. A key with ':' names a library's grant, and no referenced library has the alias '{prefix}'. Key the application's own rights without ':'."));
            }
            else if (!removes)
            {
                problems.Add(Composition(appName, $"states the right '{key}', which '{prefix}' ships. The application never changes a library's grant: remove it with {{\"key\": \"{key}\", \"$remove\": true}} and add its own under a key without ':'."));
            }
            else if (!shipped.Contains(key))
            {
                problems.Add(Composition(appName, $"removes '{key}', which '{prefix}' does not ship. A removal that removes nothing would hide a renamed grant."));
            }
        }
    }

    private static SparkSecurityGrant ToGrant(SparkJsonObject element, string? library, string layer, List<SparkSecurityProblem> problems)
    {
        var key = ((SparkJsonString)Get(element, KeyMember)!).Value;
        var resource = Get(element, ResourceMember) is SparkJsonString r ? r.Value : string.Empty;
        if (resource.Length == 0)
            problems.Add(Composition(layer, $"right '{key}' states no resource."));

        var group = string.Empty;
        switch (Get(element, GroupMember))
        {
            case SparkJsonString g when g.Value.Length > 0:
                group = g.Value;
                break;
            default:
                problems.Add(new SparkSecurityProblem(SparkSecurityProblemKind.Unresolved, layer, $"right '{key}' ({resource}) names no group: '{GroupMember}' is a group id, {AnonymousToken}, {AuthenticatedToken} or a slot."));
                break;
        }

        return new SparkSecurityGrant(key, resource, group, Flag(element, "isDenied", key, layer, problems), Flag(element, "isImportant", key, layer, problems), library);
    }

    private static bool Flag(SparkJsonObject element, string name, string key, string layer, List<SparkSecurityProblem> problems)
    {
        switch (Get(element, name))
        {
            case null:
            case SparkJsonNull:
                return false;
            case SparkJsonBoolean b:
                return b.Value;
            default:
                problems.Add(Composition(layer, $"right '{key}' states '{name}' as something other than true or false."));
                return false;
        }
    }

    private static List<KeyValuePair<string, string>> ReadGroups(SparkJsonObject? root, string appName, List<SparkSecurityProblem> problems)
    {
        var groups = new List<KeyValuePair<string, string>>();
        if (root is null || Get(root, "groups") is not SparkJsonObject block) return groups;
        foreach (var member in block.Members)
        {
            if (SparkLayers.IsAnnotation(member.Key, isRoot: false)) continue;
            if (member.Value is SparkJsonString name)
                groups.Add(new KeyValuePair<string, string>(member.Key, name.Value));
            else
                problems.Add(Composition(appName, $"declares group '{member.Key}' with a value that is not its name (a string)."));
        }
        return groups;
    }

    private static Dictionary<string, string>? ReadStrings(SparkJsonObject? root, string member, string appName, List<SparkSecurityProblem> problems)
    {
        if (root is null || Get(root, member) is not SparkJsonObject block) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Members)
        {
            if (SparkLayers.IsAnnotation(entry.Key, isRoot: false)) continue;
            if (entry.Value is SparkJsonString value)
                result[entry.Key] = value.Value;
            else
                problems.Add(Composition(appName, $"states '{member}.{entry.Key}' as something other than a string."));
        }
        return result;
    }

    private static Dictionary<string, List<string>>? ReadBindings(SparkJsonObject? root, string appName, HashSet<string> aliases, List<SparkSecurityProblem> problems)
    {
        if (root is null || Get(root, "bindings") is not SparkJsonObject block) return null;
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Members)
        {
            if (SparkLayers.IsAnnotation(entry.Key, isRoot: false)) continue;
            if (!IsSlot(entry.Key, out var alias))
            {
                problems.Add(Composition(appName, $"binds '{entry.Key}', which is not a slot ('<alias>:<slot>', both lower-kebab)."));
                continue;
            }
            if (!aliases.Contains(alias))
            {
                problems.Add(Composition(appName, $"binds '{entry.Key}', and no referenced library has the alias '{alias}'."));
                continue;
            }
            if (entry.Value is not SparkJsonArray values || values.Items.Count == 0 || values.Items.Any(v => v is not SparkJsonString))
            {
                problems.Add(Composition(appName, $"binds '{entry.Key}' to something other than a non-empty array of group ids or names."));
                continue;
            }
            result[entry.Key] = values.Items.Cast<SparkJsonString>().Select(v => v.Value).ToList();
        }
        return result;
    }

    private static Dictionary<string, bool>? ReadLibraries(SparkJsonObject? root, string appName, HashSet<string> aliases, List<SparkSecurityProblem> problems)
    {
        if (root is null || Get(root, "libraries") is not SparkJsonObject block) return null;
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in block.Members)
        {
            if (SparkLayers.IsAnnotation(entry.Key, isRoot: false)) continue;
            if (!aliases.Contains(entry.Key))
            {
                var known = aliases.Count == 0 ? "none" : string.Join(", ", aliases.OrderBy(a => a, StringComparer.Ordinal));
                problems.Add(Composition(appName, $"switches '{entry.Key}' in \"libraries\", which is not a referenced library's alias (referenced: {known})."));
                continue;
            }
            if (entry.Value is not SparkJsonBoolean on)
            {
                problems.Add(Composition(appName, $"switches '{entry.Key}' in \"libraries\" with something other than true or false."));
                continue;
            }
            result[entry.Key] = on.Value;
        }
        return result;
    }

    /// <summary>The id of the group <paramref name="value"/> names by id or by name (names ignore case, D24), or <see langword="null"/>.</summary>
    private static string? GroupReference(string value, IEnumerable<KeyValuePair<string, string>> groups)
    {
        foreach (var group in groups)
            if (string.Equals(group.Key, value, StringComparison.OrdinalIgnoreCase)) return group.Key;
        foreach (var group in groups)
            if (string.Equals(group.Value, value, StringComparison.OrdinalIgnoreCase)) return group.Key;
        return null;
    }

    private static string KeyMemberSpelling(SparkJsonObject element)
        => element.Members.First(m => Is(m.Key, KeyMember)).Key;

    /// <summary>A member by name ignoring case, as the loader has always read the file.</summary>
    private static SparkJsonNode? Get(SparkJsonObject obj, string name)
    {
        foreach (var member in obj.Members)
            if (Is(member.Key, name)) return member.Value;
        return null;
    }

    private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsKebab(string value)
    {
        if (value.Length == 0 || value[0] < 'a' || value[0] > 'z' || value[value.Length - 1] == '-') return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (c == '-' && value[i - 1] != '-');
            if (!ok) return false;
        }
        return true;
    }

    private static SparkSecurityProblem GuardRail(string alias, string message) => new(SparkSecurityProblemKind.GuardRail, alias, message);

    private static SparkSecurityProblem Composition(string layer, string message) => new(SparkSecurityProblemKind.Composition, layer, message);
}
