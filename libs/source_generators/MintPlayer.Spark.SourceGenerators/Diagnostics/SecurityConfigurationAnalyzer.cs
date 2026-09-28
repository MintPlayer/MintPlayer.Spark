using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using MintPlayer.Spark.SourceGenerators.Json;
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

    internal static readonly DiagnosticDescriptor ThreeSegmentResourceRule = new(
        id: "SPARK014",
        title: "Security resource has three segments and can never match",
        messageFormat: "'{0}' has more than one '/'. A resource is '{{action}}/{{target}}' and is split on the FIRST slash only, so the target becomes '{1}' — which names nothing. Per-attribute rights are not expressed this way",
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
        [UnknownActionRule, UnknownTargetRule, DanglingGroupRule, ThreeSegmentResourceRule, WildcardRightRule];

    /// <summary>The verbs the framework itself asks for. Anything else must be a declared custom action.</summary>
    /// <remarks>
    /// <c>Restore</c>, <c>Purge</c> and <c>ViewDeleted</c> are asked for by core on behalf of the
    /// SoftDelete package (#460): core gates a restore and a purge under their own names, and the
    /// soft-delete row policy asks <c>ViewDeleted</c> before honouring a query's <c>deleted</c> mode.
    /// <c>History</c> and <c>Revert</c> are the History package's (M7): core gates a revert under
    /// <c>Revert</c>, the package gates revision reads under <c>History</c>.
    /// </remarks>
    private static readonly string[] BuiltInActions = ["Query", "Read", "New", "Edit", "Delete", "Replicate", "Restore", "Purge", "ViewDeleted", "History", "Revert"];

    private static readonly string[] CombinedActions =
    [
        "EditNew", "EditNewDelete", "NewDelete", "QueryRead", "QueryReadEdit", "QueryReadEditNew",
        "QueryReadEditNewDelete", "ReadEdit", "ReadEditNew", "ReadEditNewDelete",
    ];

    /// <summary>Targets the framework owns, which have no model file.</summary>
    private static readonly string[] ReservedTargets = ["LookupReferences"];

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
            var knownTargets = ReadModelNames(end.Options.AdditionalFiles);

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
                    end.ReportDiagnostic(Diagnostic.Create(
                        ThreeSegmentResourceRule, location, right.Resource, target));
                    continue;
                }

                if (!IsKnownAction(action, customActions) && !authorized.ContainsKey(action))
                {
                    end.ReportDiagnostic(Diagnostic.Create(
                        UnknownActionRule, location, right.Resource, action, string.Join(", ", BuiltInActions)));
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
        });
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

    private static bool IsKnownAction(string action, ISet<string> customActions)
        => BuiltInActions.Contains(action, StringComparer.OrdinalIgnoreCase)
           || CombinedActions.Contains(action, StringComparer.OrdinalIgnoreCase)
           || customActions.Contains(action);

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

    /// <summary>Every persistent-object name and query alias the model declares.</summary>
    private static ISet<string> ReadModelNames(IEnumerable<AdditionalText> files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reserved in ReservedTargets) names.Add(reserved);

        var any = false;
        foreach (var file in files)
        {
            if (file.Path.IndexOf("App_Data", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (file.Path.IndexOf("Model", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (file.GetText() is not { } text) continue;

            any = true;
            var content = text.ToString();
            foreach (var name in ModelNames(content))
                names.Add(name);

            // A lookupReferenceType names a DynamicLookupReference class rather than a persistent
            // object, and rights are granted on it exactly like any other target. It has no model
            // file of its own, so without this every lookup-backed right reads as a typo.
            foreach (var name in LookupReferenceTypes(content))
                names.Add(name);
        }

        // No model files visible means nothing to compare against, and reporting every target as
        // unknown would be worse than reporting none.
        return any ? names : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
