using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Reports rights in <c>App_Data/security.json</c> that load cleanly and grant nothing.
/// </summary>
/// <remarks>
/// <para>
/// The runtime validator checks that a resource has the <c>{action}/{target}</c> shape and that no
/// two rights share an id. It does not check that either half <em>means</em> anything — so
/// <c>"Raed/Person"</c> parses, loads, and silently matches no request forever. Nothing fails; the
/// permission simply never applies, and the only symptom is a user who cannot do something they were
/// granted.
/// </para>
/// <para>
/// A compile-time check is coherent here specifically because <c>security.json</c> is hand-authored
/// and never machine-written. The model files are not: they are rewritten by the model synchronize
/// command, which runs <em>after</em> compilation, so an analyzer over them is permanently one build
/// behind — a design this repository evaluated and rejected once already. That is why this analyzer
/// reads model files only for the NAMES it cross-references, and reports nothing about them.
/// </para>
/// <para>
/// The runtime remains authoritative. This exists to catch what the runtime cannot see: a file that
/// is valid and wrong.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SecurityConfigurationAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor UnknownActionRule = new(
        id: "SPARK011",
        title: "Security right names an action Spark never asks for",
        messageFormat: "'{0}' names the action '{1}', which is not a built-in ({2}), a combined name, or a declared custom action. Spark never asks for it, so this right can never match — check the spelling against customActions.json",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnknownTargetRule = new(
        id: "SPARK012",
        title: "Security right names a type no model file declares",
        messageFormat: "'{0}' targets '{1}', which is not a persistent object, query alias or reserved target in this application. The right will never match anything — check the spelling against App_Data/Model",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DanglingGroupRule = new(
        id: "SPARK013",
        title: "Security right is granted to a group that is not declared",
        messageFormat: "'{0}' is granted to group '{1}', which the 'groups' block does not declare. Nobody is ever in a group that does not exist, so this right can never apply",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <remarks>
    /// The attribute-level right validator (#460 contributions, PRD §5 Q12). It used to report every
    /// three-segment resource as unmatchable; <c>{verb}/{Type}/{Attr}</c> is now the attribute-right
    /// syntax, so this reports only the ones the host refuses at startup — hence an error.
    /// </remarks>
    internal static readonly DiagnosticDescriptor AttributeRightRule = new(
        id: "SPARK014",
        title: "Attribute-level right is invalid",
        messageFormat: "'{0}' {1}. The host refuses this security.json at startup",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <remarks>
    /// The stale-deny trap (PRD §5 Q13): a group restricts a verb on some attributes of a type, and an
    /// attribute the restriction does not mention — typically one added later — silently keeps the
    /// type-level grant. A warning, because leaving it there can be deliberate.
    /// </remarks>
    internal static readonly DiagnosticDescriptor StaleAttributeDenyRule = new(
        id: "SPARK024",
        title: "Attribute-level restriction leaves other attributes on the type-level right",
        messageFormat: "Group '{0}' restricts {1} on {2} of {3} attributes of '{4}'; {5} {6} still {7} through the type-level right — intended?",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <remarks>
    /// An error rather than a warning because the runtime refuses the same file at startup: the
    /// build is only reporting early what the host would report on its first run.
    /// </remarks>
    internal static readonly DiagnosticDescriptor WildcardRightRule = new(
        id: "SPARK021",
        title: "Security right uses a wildcard, which is not supported",
        messageFormat: "'{0}' uses the wildcard '*'. Wildcard rights are refused at startup: an access review must be able to enumerate who can do what, and a wildcard covers types and actions that do not exist yet. Name the target, and use a combined action (for example 'QueryReadEditNewDelete/Person') to cover several actions",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [UnknownActionRule, UnknownTargetRule, DanglingGroupRule, AttributeRightRule, WildcardRightRule, StaleAttributeDenyRule];

    /// <summary>The verbs with an attribute-level form (mirrors <c>SparkAttributeRights.Verbs</c>).</summary>
    private static readonly string[] AttributeVerbs = ["Query", "Read", "Edit", "New"];

    // The verbs Spark asks for are no longer a hand-kept list here (#460 contributions, Q2): core,
    // SoftDelete, History and Moderation each declare theirs with [assembly: SparkReservedActions(...)],
    // and ReservedActionsReader reads them from the compilation's references. A package's verbs are
    // therefore known exactly where the package is referenced — the Moderation verbs no longer read as
    // SPARK011 because a list here forgot them (QnA, M13), and a Restore right in an app without
    // SoftDelete is now reported, since nothing there ever asks for it.

    /// <summary>Targets the framework owns, which have no model file.</summary>
    /// <remarks><c>Moderation</c>: the Moderation package's own surface (<c>Review</c>, <c>Suspend</c>, <c>Audit</c>), T6.</remarks>
    private static readonly string[] ReservedTargets = ["LookupReferences", "Moderation"];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var security = context.Options.AdditionalFiles
            .FirstOrDefault(f => IsNamed(f.Path, "security.json"));

        // Fail-soft on purpose: a project that has not wired security.json as an AdditionalFile gets
        // no diagnostics rather than a false one. spark.targets wires it for every consumer.
        if (security is null) return;

        var reserved = ReservedActionsReader.Read(context.Compilation);
        var builtInActions = reserved.Where(r => !r.IsCombined).Select(r => r.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var knownActions = new HashSet<string>(reserved.Select(r => r.Verb), StringComparer.OrdinalIgnoreCase);

        // [SparkAuthorize("Manage", "UploadToken")] declares a resource that exists nowhere in the
        // model: the action is a verb the application invented and the target names a controller's
        // surface rather than a persistent object. Both halves are legitimate and both looked like
        // typos to the first version of this analyzer, which reported seven of them against the
        // production app on its first run. They live in the compilation, so read them from there.
        var authorized = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var authorizedTargets = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        context.RegisterSymbolAction(
            symbol => CollectSparkAuthorize(symbol.Symbol, authorized, authorizedTargets),
            SymbolKind.NamedType, SymbolKind.Method);

        context.RegisterCompilationEndAction(end =>
        {
            var text = security.GetText(end.CancellationToken);
            if (text is null) return;

            var content = text.ToString();
            var rights = SecurityJsonReader.ReadRights(content);
            if (rights.Count == 0) return;

            var groupIds = SecurityJsonReader.ReadGroupIds(content);
            var customActions = ReadNames(end.Options.AdditionalFiles, "customActions.json", TopLevelKeys);
            var model = ReadModel(end.Options.AdditionalFiles);
            var knownTargets = model.Targets;
            var combinedVerbs = reserved.Where(r => r.IsCombined).Select(r => r.Verb).ToArray();
            var staleDeny = new StaleDenyCollector();

            foreach (var right in rights)
            {
                var location = Location.Create(
                    security.Path,
                    new TextSpan(right.ResourceStart, right.ResourceLength),
                    text.Lines.GetLinePositionSpan(new TextSpan(right.ResourceStart, right.ResourceLength)));

                if (right.Resource.IndexOf('*') >= 0)
                {
                    end.ReportDiagnostic(Diagnostic.Create(WildcardRightRule, location, right.Resource));
                    continue;
                }

                var slash = right.Resource.IndexOf('/');
                if (slash < 0) continue; // Shape is the runtime validator's job, and it refuses this.

                var action = right.Resource.Substring(0, slash);
                var target = right.Resource.Substring(slash + 1);

                if (target.IndexOf('/') >= 0)
                {
                    var problem = AttributeRightProblem(
                        action, target, combinedVerbs, model, end.Compilation, out var expanded, out var typeName, out var attributeName);

                    if (problem is not null)
                        end.ReportDiagnostic(Diagnostic.Create(AttributeRightRule, location, right.Resource, problem));
                    else if (expanded is not null)
                        staleDeny.Add(right, location, expanded, typeName!, attributeName!);

                    if (right.GroupId.Length > 0 && groupIds.Count > 0 && !groupIds.Contains(right.GroupId))
                    {
                        end.ReportDiagnostic(Diagnostic.Create(
                            DanglingGroupRule, location, right.Resource, right.GroupId));
                    }

                    continue;
                }

                if (!knownActions.Contains(action) && !customActions.Contains(action) && !authorized.ContainsKey(action))
                {
                    end.ReportDiagnostic(Diagnostic.Create(
                        UnknownActionRule, location, right.Resource, action, string.Join(", ", builtInActions)));
                }

                // Only when the model is actually visible: an application that has not wired its
                // model files would otherwise have every right reported.
                //
                // Replicate is excluded because its target is a RavenDB COLLECTION name, not a model
                // type — the replication endpoint asks IsAllowedAsync("Replicate", script.SourceCollection),
                // and a collection is conventionally the plural of the type. Judging it against model
                // names reported every correct replication right in two demo apps.
                var judgeTarget = !string.Equals(action, "Replicate", StringComparison.OrdinalIgnoreCase);

                if (judgeTarget && knownTargets.Count > 0
                    && !knownTargets.Contains(target) && !authorizedTargets.ContainsKey(target))
                {
                    end.ReportDiagnostic(Diagnostic.Create(
                        UnknownTargetRule, location, right.Resource, target));
                }

                if (right.GroupId.Length > 0 && groupIds.Count > 0 && !groupIds.Contains(right.GroupId))
                {
                    end.ReportDiagnostic(Diagnostic.Create(
                        DanglingGroupRule, location, right.Resource, right.GroupId));
                }
            }

            if (model.Types.Count > 0)
            {
                var groupNames = ModelNamesReader.ReadGroupNames(content);
                foreach (var finding in staleDeny.Findings(model, end.Compilation))
                {
                    var groupName = groupNames.TryGetValue(finding.GroupId, out var n) ? n : finding.GroupId;
                    end.ReportDiagnostic(Diagnostic.Create(
                        StaleAttributeDenyRule, finding.Location,
                        groupName, finding.Verb, finding.Restricted, finding.Total, finding.Type,
                        string.Join(", ", finding.Unmentioned.Select(a => "'" + a + "'")),
                        finding.Unmentioned.Count == 1 ? "is" : "are",
                        Adjective(finding.Verb)));
                }
            }
        });
    }

    private static string Adjective(string verb) => verb switch
    {
        "Query" => "queryable",
        "Read" => "readable",
        "Edit" => "editable",
        "New" => "settable on create",
        _ => "allowed for " + verb,
    };

    /// <summary>
    /// Why an attribute-level resource is refused, or null when it is valid. On success,
    /// <paramref name="expanded"/> holds the concrete verbs it stands for and the type and attribute
    /// in the model's spelling (null when the model is not visible, so nothing more can be said).
    /// </summary>
    private static string? AttributeRightProblem(
        string action, string target, string[] combinedVerbs, ModelIndex model, Compilation compilation,
        out string[]? expanded, out string? typeName, out string? attributeName)
    {
        expanded = null;
        typeName = attributeName = null;

        var slash = target.IndexOf('/');
        var type = target.Substring(0, slash);
        var attribute = target.Substring(slash + 1);

        if (type.Length == 0 || attribute.Length == 0 || attribute.IndexOf('/') >= 0)
        {
            return "is not '{verb}/{Type}/{Attribute}' — an attribute of an AsDetail row is targeted on the row "
                   + "type (for example 'Edit/Lyrics/Text'), never with a longer path";
        }

        var verbs = ExpandAttributeAction(action, combinedVerbs);
        if (verbs is null)
        {
            var combined = combinedVerbs.Where(c => ExpandAttributeAction(c, combinedVerbs) is not null)
                .OrderBy(c => c, StringComparer.Ordinal);
            return $"uses '{action}', which has no attribute-level form: only {string.Join(", ", AttributeVerbs)} and "
                   + $"the combined verbs made solely of them ({string.Join(", ", combined)}) take a third segment. "
                   + $"Delete, custom actions and every other verb stay type-level — write '{action}/{type}'";
        }

        // Nothing to compare against: say nothing more, as SPARK012 does.
        if (model.Types.Count == 0)
            return null;

        if (!model.Types.TryGetValue(type, out var entry))
        {
            // A type added in this very build has no model file until synchronize runs after it.
            // Judge it by its class instead, rather than blocking the build that sync needs. Matched
            // ignoring case, as the runtime validator matches the model (and its satellite fallback).
            var declared = compilation.GetSymbolsWithName(n => string.Equals(n, type, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type)
                .OfType<INamedTypeSymbol>().FirstOrDefault();
            if (declared is null)
            {
                return $"targets '{type}', which is not a persistent object in App_Data/Model — an attribute right "
                       + "names the type by its name, not an alias, a query or a reserved target";
            }

            entry = new ModelType(declared.Name, null);
        }

        var known = KnownAttributes(entry, compilation);
        var match = known.FirstOrDefault(a => string.Equals(a, attribute, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return $"names the attribute '{attribute}', which '{entry.Name}' does not declare "
                   + $"(its attributes: {string.Join(", ", known.OrderBy(a => a, StringComparer.Ordinal).Take(20))})";
        }

        expanded = verbs;
        typeName = entry.Name;
        attributeName = match;
        return null;
    }

    /// <summary>
    /// The concrete verbs an action with an attribute form stands for, or null when it has none. A
    /// combined verb is recognised only by the reserved-verb table and decomposed by its PascalCase
    /// parts (<c>QueryReadEdit</c> → Query, Read, Edit), every one of which must have an attribute form.
    /// </summary>
    private static string[]? ExpandAttributeAction(string action, string[] combinedVerbs)
    {
        var single = AttributeVerbs.FirstOrDefault(v => string.Equals(v, action, StringComparison.OrdinalIgnoreCase));
        if (single is not null)
            return [single];

        var combined = combinedVerbs.FirstOrDefault(c => string.Equals(c, action, StringComparison.OrdinalIgnoreCase));
        if (combined is null)
            return null;

        var parts = new List<string>();
        var start = 0;
        for (var i = 1; i <= combined.Length; i++)
        {
            if (i < combined.Length && !char.IsUpper(combined[i])) continue;
            var part = combined.Substring(start, i - start);
            var verb = AttributeVerbs.FirstOrDefault(v => string.Equals(v, part, StringComparison.Ordinal));
            if (verb is null) return null;
            parts.Add(verb);
            start = i;
        }

        return parts.Count == 0 ? null : parts.ToArray();
    }

    /// <summary>
    /// The model file's attributes, plus the public instance properties of its CLR type: the model is
    /// one build behind (synchronize runs after compilation), and a property added in this build is an
    /// attribute the next synchronize writes — the same set the generated <c>AttributeNames</c> lists.
    /// </summary>
    private static IReadOnlyList<string> KnownAttributes(ModelType entry, Compilation compilation)
    {
        var names = new HashSet<string>(entry.Attributes, StringComparer.OrdinalIgnoreCase);

        INamedTypeSymbol? symbol = null;
        if (!string.IsNullOrEmpty(entry.ClrType))
            symbol = compilation.GetTypeByMetadataName(entry.ClrType!);
        symbol ??= compilation.GetSymbolsWithName(n => string.Equals(n, entry.Name, StringComparison.Ordinal), SymbolFilter.Type)
            .OfType<INamedTypeSymbol>().FirstOrDefault();

        for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (property.Name == "Id" || property.IsIgnoredForSparkModel())
                    continue;
                names.Add(property.Name);
            }
        }

        return names.ToList();
    }

    /// <summary>
    /// Collects attribute-level rights per (group, verb, type) for SPARK024: a type with at least one
    /// attribute DENY for the group and verb, and attributes no attribute right of that group mentions
    /// for that verb, is reported once, on the first deny.
    /// </summary>
    private sealed class StaleDenyCollector
    {
        private readonly Dictionary<(string Group, string Verb, string Type), (Location First, HashSet<string> Denied)> denied = new(TupleComparer.Instance);
        private readonly Dictionary<(string Group, string Verb, string Type), HashSet<string>> mentioned = new(TupleComparer.Instance);

        public void Add(SecurityJsonReader.RightEntry right, Location location, string[] verbs, string type, string attribute)
        {
            foreach (var verb in verbs)
            {
                Mention(right.GroupId, verb, type, attribute);

                if (right.IsDenied)
                {
                    var key = (right.GroupId, verb, type);
                    if (!denied.TryGetValue(key, out var entry))
                        denied[key] = entry = (location, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    entry.Denied.Add(attribute);
                }
                else if (verb == "Read")
                {
                    // A grant mentions what it implies (Read ⇒ Query), as the runtime index expands it.
                    Mention(right.GroupId, "Query", type, attribute);
                }
            }
        }

        private void Mention(string group, string verb, string type, string attribute)
        {
            var key = (group, verb, type);
            if (!mentioned.TryGetValue(key, out var set))
                mentioned[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(attribute);
        }

        public IEnumerable<StaleDenyFinding> Findings(ModelIndex model, Compilation compilation)
        {
            foreach (var pair in denied)
            {
                var (group, verb, type) = pair.Key;
                if (!model.Types.TryGetValue(type, out var entry))
                    continue;

                var all = KnownAttributes(entry, compilation);
                var seen = mentioned[pair.Key];
                var unmentioned = all.Where(a => !seen.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
                if (unmentioned.Count == 0)
                    continue;

                yield return new StaleDenyFinding(pair.Value.First, group, verb, entry.Name, pair.Value.Denied.Count, all.Count, unmentioned);
            }
        }
    }

    private sealed class StaleDenyFinding(
        Location location, string groupId, string verb, string type, int restricted, int total, List<string> unmentioned)
    {
        public Location Location { get; } = location;
        public string GroupId { get; } = groupId;
        public string Verb { get; } = verb;
        public string Type { get; } = type;
        public int Restricted { get; } = restricted;
        public int Total { get; } = total;
        public List<string> Unmentioned { get; } = unmentioned;
    }

    private sealed class TupleComparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((string, string, string) x, (string, string, string) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Item3, y.Item3);

        public int GetHashCode((string, string, string) obj)
        {
            unchecked
            {
                var h = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1);
                h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2);
                return h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item3);
            }
        }
    }

    /// <summary>One persistent object as its model file declares it.</summary>
    private sealed class ModelType(string name, string? clrType)
    {
        public string Name { get; } = name;
        public string? ClrType { get; } = clrType;
        public List<string> Attributes { get; } = new();
    }

    /// <summary>What the model files declare: the targets a right may name, and the persistent objects by name.</summary>
    private sealed class ModelIndex
    {
        public ISet<string> Targets { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ModelType> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Harvests the two strings from every <c>[SparkAuthorize(action, target)]</c> in the
    /// compilation. Both halves are application-invented and neither appears in the model, so
    /// without this every controller-guarded right reads as a typo.
    /// </summary>
    private static void CollectSparkAuthorize(
        ISymbol symbol,
        ConcurrentDictionary<string, byte> actions,
        ConcurrentDictionary<string, byte> targets)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name is not ("SparkAuthorizeAttribute" or "SparkAuthorize"))
                continue;

            var args = attribute.ConstructorArguments;
            if (args.Length > 0 && args[0].Value is string action && action.Length > 0)
                actions.TryAdd(action, 0);
            if (args.Length > 1 && args[1].Value is string target && target.Length > 0)
                targets.TryAdd(target, 0);
        }
    }

    private static bool IsNamed(string path, string fileName)
        => path.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase);

    private static ISet<string> ReadNames(
        IEnumerable<AdditionalText> files, string fileName, Func<string, IEnumerable<string>> extract)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var file = files.FirstOrDefault(f => IsNamed(f.Path, fileName));
        if (file?.GetText() is { } text)
        {
            foreach (var name in extract(text.ToString()))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Every persistent-object name and alias, query name and alias, and lookup-reference type the
    /// model declares — read by position (<see cref="ModelNamesReader"/>), so an attribute, tab or
    /// group name no longer counts as a type target — plus each persistent object's attributes.
    /// </summary>
    private static ModelIndex ReadModel(IEnumerable<AdditionalText> files)
    {
        var index = new ModelIndex();
        foreach (var reserved in ReservedTargets) index.Targets.Add(reserved);

        var any = false;
        foreach (var file in files)
        {
            if (file.Path.IndexOf("App_Data", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (file.Path.IndexOf("Model", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (file.GetText() is not { } text) continue;

            any = true;
            var content = text.ToString();

            if (ModelNamesReader.Read(content) is { } parsed)
            {
                if (!string.IsNullOrEmpty(parsed.TypeName))
                {
                    index.Targets.Add(parsed.TypeName!);
                    var type = new ModelType(parsed.TypeName!, parsed.ClrType);
                    type.Attributes.AddRange(parsed.Attributes);
                    index.Types[parsed.TypeName!] = type;
                }

                if (!string.IsNullOrEmpty(parsed.Alias))
                    index.Targets.Add(parsed.Alias!);

                // Query names/aliases, and lookupReferenceTypes: a DynamicLookupReference class has no
                // model file of its own, and rights are granted on it like any other target.
                foreach (var name in parsed.OtherTargets)
                    index.Targets.Add(name);

                continue;
            }

            // Not parseable as a model file: fall back to the permissive by-key scan, so a file this
            // reader cannot follow produces no false SPARK012 — the runtime loader is authoritative.
            foreach (var name in ModelNames(content))
                index.Targets.Add(name);
            foreach (var name in LookupReferenceTypes(content))
                index.Targets.Add(name);
        }

        // No model files visible means nothing to compare against, and reporting every target as
        // unknown would be worse than reporting none.
        if (!any)
            index.Targets.Clear();

        return index;
    }

    private static IEnumerable<string> ModelNames(string json)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     json, @"""(?:name|alias)""\s*:\s*""(?<v>[A-Za-z_][A-Za-z0-9_\-]*)"""))
        {
            yield return m.Groups["v"].Value;
        }
    }

    private static IEnumerable<string> LookupReferenceTypes(string json)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     json, @"""lookupReferenceType""\s*:\s*""(?<v>[A-Za-z_][A-Za-z0-9_\.]*)"""))
        {
            var value = m.Groups["v"].Value;
            var dot = value.LastIndexOf('.');
            yield return dot < 0 ? value : value.Substring(dot + 1);
        }
    }

    /// <summary>
    /// The keys of the root object. Found by tracking nesting depth rather than indentation, so a
    /// tab-indented, four-space or minified file reads the same as the two-space one the old
    /// <c>^\s{2}"</c> pattern assumed — under which every custom right read as unknown.
    /// </summary>
    private static IEnumerable<string> TopLevelKeys(string json)
    {
        var depth = 0;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '{' || c == '[') { depth++; continue; }
            if (c == '}' || c == ']') { depth--; continue; }
            if (c != '"') continue;

            var start = i + 1;
            for (i = start; i < json.Length && json[i] != '"'; i++)
            {
                if (json[i] == '\\') i++;
            }
            if (depth != 1 || i >= json.Length) continue;

            var key = json.Substring(start, i - start);
            var next = i + 1;
            while (next < json.Length && char.IsWhiteSpace(json[next])) next++;

            if (next < json.Length && json[next] == ':'
                && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$"))
                yield return key;
        }
    }
}
