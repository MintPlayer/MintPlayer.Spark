using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Naming;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK056: a writable <c>[Reference(typeof(T))]</c> that names no query, where no <c>SparkContext</c> in the
/// compilation has a <b>plain entity root</b> of <c>T</c> (<c>docs/default_index_PRD.md</c>, D6, D8).
/// <para>
/// The synchronizer derives such a reference's query from the context's roots (D6): the plain root of <c>T</c> mints
/// <c>Database.{Plural}</c>, and the reference resolves to it. Without one the attribute gets no <c>query</c> at all,
/// and the edit page's picker has no options — SeoTester's empty Organization picker (PRD §1), which nothing warned
/// about.
/// </para>
/// <para>
/// What counts as a plain entity root: an <c>IRavenQueryable&lt;T&gt;</c> property of a context, hand-written or inherited
/// from a base in this compilation, whose body is the plain <c>Session.Query&lt;T&gt;()</c> (the generator's own
/// <see cref="Naming.PlainRootClassifier"/>), or a <c>[SparkPlainRoot(typeof(T), …)]</c> marker on a context or a base
/// context (a referenced base carries its own). A filtered or customized root does not count, and neither does a
/// <b>view</b> root (<c>IRavenQueryable&lt;VT&gt;</c> over a <c>[FromIndex]</c> class), which mints no query. When
/// <c>T</c> has several roots, the reference resolves to the plain one (SP3), so several roots are fine.
/// </para>
/// <para>
/// It runs on the compilation <b>after</b> generation, the shape <c>csc</c> hands the analyzer driver, so the
/// generator's own context members are seen too. Read-only references (<c>[ReadOnly(true)]</c>, or no setter) never
/// open a picker and are skipped, as are generated members (the audit members are both) and views.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingReferenceRootAnalyzer : DiagnosticAnalyzer
{
    private const string SparkContextFullName = "MintPlayer.Spark.SparkContext";
    private const string ReferenceAttributeFullName = "MintPlayer.Spark.Abstractions.ReferenceAttribute";
    private const string FromIndexAttributeFullName = "MintPlayer.Spark.Abstractions.FromIndexAttribute";
    private const string SparkPlainRootAttributeFullName = "MintPlayer.Spark.Abstractions.SparkPlainRootAttribute";
    private const string ReadOnlyAttributeFullName = "System.ComponentModel.ReadOnlyAttribute";
    private const string RavenQueryableMetadataName = "Raven.Client.Documents.Linq.IRavenQueryable`1";

    /// <summary>Diagnostic property: the referenced entity type's metadata name, for the code fix.</summary>
    internal const string EntityTypeProperty = "EntityType";

    /// <summary>Diagnostic property: the context the code fix adds the root to.</summary>
    internal const string ContextTypeProperty = "ContextType";

    internal static readonly DiagnosticDescriptor MissingRootRule = new(
        id: "SPARK056",
        title: "Reference without a query to a type the context has no plain entity root of",
        messageFormat: "'{0}' references '{1}' without naming a query, and '{2}' has no plain entity root of '{1}' (one whose body is Session.Query<{3}>()), so the reference gets no query and its picker no options. Add 'public IRavenQueryable<{3}> {4} => Session.Query<{3}>();' to '{2}', or name a query in [Reference(typeof({3}), \"…\")]",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(MissingRootRule);

    private readonly struct Candidate
    {
        public Candidate(IPropertySymbol property, INamedTypeSymbol target, Location location)
        {
            Property = property;
            Target = target;
            Location = location;
        }

        public IPropertySymbol Property { get; }
        public INamedTypeSymbol Target { get; }
        public Location Location { get; }
    }

    public override void Initialize(AnalysisContext context)
    {
        // Analyze: the generated partial of the context carries the [SparkPlainRoot] markers. Candidates in generated
        // files are skipped by hand below, because their locations are unfixable.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var sparkContext = compilationStart.Compilation.GetTypeByMetadataName(SparkContextFullName);
            var ravenQueryable = compilationStart.Compilation.GetTypeByMetadataName(RavenQueryableMetadataName);
            if (sparkContext is null || ravenQueryable is null) return;

            var contexts = new ConcurrentBag<INamedTypeSymbol>();
            var candidates = new ConcurrentBag<Candidate>();

            compilationStart.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (!type.IsAbstract && type.TypeKind == TypeKind.Class && DerivesFrom(type, sparkContext))
                    contexts.Add(type);
            }, SymbolKind.NamedType);

            compilationStart.RegisterSymbolAction(symbolContext =>
            {
                var property = (IPropertySymbol)symbolContext.Symbol;
                if (Describe(property) is { } candidate)
                    candidates.Add(candidate);
            }, SymbolKind.Property);

            compilationStart.RegisterCompilationEndAction(endContext =>
            {
                // A library has no context: its references are judged in the application that roots them.
                var allContexts = contexts
                    .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                    .OrderBy(c => c.ToDisplayString(), System.StringComparer.Ordinal)
                    .ToList();
                if (allContexts.Count == 0) return;

                var rooted = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var contextType in allContexts)
                    CollectPlainRoots(contextType, ravenQueryable, endContext.Compilation, rooted, endContext.CancellationToken);

                var reportOn = allContexts[0];
                foreach (var candidate in candidates)
                {
                    if (rooted.Contains(candidate.Target)) continue;

                    var plural = IndexNaming.Pluralize(candidate.Target.Name);
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add(EntityTypeProperty, MetadataName(candidate.Target))
                        .Add(ContextTypeProperty, MetadataName(reportOn));

                    endContext.ReportDiagnostic(Diagnostic.Create(
                        MissingRootRule, candidate.Location, properties,
                        $"{candidate.Property.ContainingType.Name}.{candidate.Property.Name}",
                        candidate.Target.ToDisplayString(),
                        reportOn.Name,
                        candidate.Target.Name,
                        plural));
                }
            });
        });
    }

    /// <summary>A writable, query-less <c>[Reference(typeof(T))]</c> on a hand-written, non-view type, or <see langword="null"/>.</summary>
    private static Candidate? Describe(IPropertySymbol property)
    {
        if (property.IsStatic || property.SetMethod is null) return null;

        var reference = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ReferenceAttributeFullName);
        if (reference is null || reference.ConstructorArguments.Length == 0) return null;
        if (reference.ConstructorArguments[0].Value is not INamedTypeSymbol target) return null;

        // [Reference(typeof(T), "Q")] names its query and overrides the lookup (D2).
        if (reference.ConstructorArguments.Length > 1 && reference.ConstructorArguments[1].Value is string query
            && !string.IsNullOrWhiteSpace(query))
            return null;

        if (property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ReadOnlyAttributeFullName
                && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is true))
            return null;

        // A view is a projection: it is never edited, so its references open no picker.
        if (property.ContainingType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FromIndexAttributeFullName))
            return null;

        var location = property.Locations.FirstOrDefault(l =>
            l.IsInSource && !l.SourceTree!.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase));
        if (location is null) return null;

        return new Candidate(property, target, location);
    }

    /// <summary>
    /// Every entity type <paramref name="contextType"/> has a <b>plain</b> root of: a root, own or inherited from a base
    /// in this compilation, that the generator's classifier calls plain (<see cref="PlainRootClassifier"/>), and every
    /// <c>[SparkPlainRoot]</c> marker on the context or a base context (a referenced base carries its own).
    /// </summary>
    /// <remarks>
    /// Any root used to count. But the synchronizer resolves a query-less reference to the plain root only (D6), so a
    /// type with only a filtered root (<c>ActiveOrganizations =&gt; Session.Query&lt;Organization&gt;().Where(…)</c>) got
    /// no query and an empty picker, with no warning. The classifier is the generator's own, not a copy, so this agrees
    /// with the markers it writes even where the generated partial is absent (a context that is not partial).
    /// </remarks>
    private static void CollectPlainRoots(
        INamedTypeSymbol contextType, INamedTypeSymbol ravenQueryable, Compilation compilation, HashSet<ITypeSymbol> rooted,
        System.Threading.CancellationToken ct)
    {
        foreach (var root in PlainRootClassifier.PlainRoots(contextType, ravenQueryable, compilation, ct))
            rooted.Add(root.Entity);

        for (var current = contextType; current is not null; current = current.BaseType)
        {
            foreach (var marker in current.GetAttributes())
            {
                if (marker.AttributeClass?.ToDisplayString() != SparkPlainRootAttributeFullName) continue;
                if (marker.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol entity)
                    rooted.Add(entity);
            }
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;

        return false;
    }

    /// <summary>The name <c>Compilation.GetTypeByMetadataName</c> resolves, nested types included (<c>Outer+Inner</c>).</summary>
    internal static string MetadataName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + name
            : name;
    }
}
