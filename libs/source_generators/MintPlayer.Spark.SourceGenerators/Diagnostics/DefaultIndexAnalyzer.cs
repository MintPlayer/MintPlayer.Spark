using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Naming;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// One <c>[DefaultIndex]</c> per collection type, within a compilation.
/// <para>
/// The authoritative check is the index catalog's freeze-time validation — it alone sees indexes
/// contributed across assemblies via <c>AddIndexesFrom(...)</c>, and it cannot be switched off. This
/// analyzer is the compile-time mirror for the common case where both claims live in the app project,
/// so the author gets a squiggle instead of a startup failure.
/// </para>
/// <para>
/// A <c>[GenerateIndex]</c> entity claims the default through its <em>generated</em> index (unless it
/// opts out with <c>IsDefault = false</c>), so the entity itself counts as a claim here — the clash
/// with a hand-written <c>[DefaultIndex]</c> must not depend on the generated tree being analyzed,
/// which host settings can skip. When the generated index <em>is</em> analyzed, it deduplicates
/// against the entity's claim by index name. Reports anchor only on hand-written declarations: the
/// generated-code filter drops diagnostics by location, and a generated location is unfixable anyway.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DefaultIndexAnalyzer : DiagnosticAnalyzer
{
    private const string DefaultIndexAttributeFullName = "MintPlayer.Spark.Abstractions.DefaultIndexAttribute";
    private const string GenerateIndexAttributeFullName = "MintPlayer.Spark.Abstractions.GenerateIndexAttribute";
    private const string RavenIndexesNamespace = "Raven.Client.Documents.Indexes";
    private const string AbstractIndexCreationTaskName = "AbstractIndexCreationTask";
    private const string AbstractMultiMapIndexCreationTaskName = "AbstractMultiMapIndexCreationTask";

    internal static readonly DiagnosticDescriptor DuplicateDefaultIndexRule = new(
        id: "SPARK009",
        title: "Multiple [DefaultIndex] markers over one collection type",
        messageFormat: "'{0}' and '{1}' both claim [DefaultIndex] for collection type '{2}'; exactly one index per entity can shape its model file. Remove one marker, or opt the generated index out with [GenerateIndex(IsDefault = false)] on the entity",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(DuplicateDefaultIndexRule);

    private readonly struct DefaultClaim
    {
        public DefaultClaim(string indexName, INamedTypeSymbol collection, Location? location)
        {
            IndexName = indexName;
            Collection = collection;
            Location = location;
        }

        public string IndexName { get; }
        public INamedTypeSymbol Collection { get; }
        public Location? Location { get; }
    }

    public override void Initialize(AnalysisContext context)
    {
        // Analyze (not None): a generated [DefaultIndex] index should still enter the walk when the host
        // analyzes generated trees, so it can deduplicate against its entity's claim.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var claims = new ConcurrentBag<DefaultClaim>();

            compilationStart.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.IsAbstract) return;

                if (type.GetAttributes().Any(a =>
                        a.AttributeClass?.ToDisplayString() == DefaultIndexAttributeFullName)
                    && CollectionTypeOf(type) is { } collection)
                {
                    claims.Add(new DefaultClaim(type.Name, collection, HandWrittenLocation(type)));
                }

                if (type.GetAttributes().FirstOrDefault(a =>
                        a.AttributeClass?.ToDisplayString() == GenerateIndexAttributeFullName) is { } generateIndex
                    && IsDefaultOf(generateIndex))
                {
                    claims.Add(new DefaultClaim(
                        GeneratedIndexNameOf(generateIndex, type),
                        type,
                        HandWrittenLocation(type)));
                }
            }, SymbolKind.NamedType);

            compilationStart.RegisterCompilationEndAction(endContext =>
            {
                foreach (var group in claims
                    .GroupBy(c => (ITypeSymbol)c.Collection, SymbolEqualityComparer.Default))
                {
                    // The generated index and its entity's [GenerateIndex] describe the same claim; when the
                    // generated tree is analyzed both are collected, so merge by index name.
                    var distinct = group
                        .GroupBy(c => c.IndexName, System.StringComparer.Ordinal)
                        .Select(g => new DefaultClaim(g.Key, g.First().Collection, g.Select(c => c.Location).FirstOrDefault(l => l is not null)))
                        .OrderBy(c => c.IndexName, System.StringComparer.Ordinal)
                        .ToList();
                    if (distinct.Count < 2) continue;

                    foreach (var claim in distinct)
                    {
                        if (claim.Location is null) continue;

                        var other = distinct.First(c => c.IndexName != claim.IndexName);
                        endContext.ReportDiagnostic(Diagnostic.Create(
                            DuplicateDefaultIndexRule, claim.Location,
                            claim.IndexName, other.IndexName, group.Key.ToDisplayString()));
                    }
                }
            });
        });
    }

    /// <summary>The attribute's <c>IsDefault</c> named argument, defaulting to <c>true</c> when absent.</summary>
    private static bool IsDefaultOf(AttributeData generateIndex)
        => generateIndex.NamedArguments.FirstOrDefault(a => a.Key == "IsDefault").Value.Value is not false;

    /// <summary>The generated index's name: the <c>IndexName</c> override, or the naming convention.</summary>
    private static string GeneratedIndexNameOf(AttributeData generateIndex, INamedTypeSymbol entity)
        => generateIndex.NamedArguments.FirstOrDefault(a => a.Key == "IndexName").Value.Value as string
            ?? IndexNaming.IndexName(entity.Name);

    /// <summary>
    /// The collection type the index maps, mirroring the runtime's base-type walk: the single generic
    /// argument of <c>AbstractIndexCreationTask&lt;T&gt;</c> or
    /// <c>AbstractMultiMapIndexCreationTask&lt;T&gt;</c>. The two-argument
    /// <c>AbstractIndexCreationTask&lt;TDocument, TReduceResult&gt;</c> derives from the one-argument
    /// form, so the walk covers it without a separate case.
    /// </summary>
    private static INamedTypeSymbol? CollectionTypeOf(INamedTypeSymbol indexType)
    {
        for (var baseType = indexType.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType
                && baseType.TypeArguments.Length == 1
                && baseType.OriginalDefinition.Name is AbstractIndexCreationTaskName or AbstractMultiMapIndexCreationTaskName
                && baseType.OriginalDefinition.ContainingNamespace?.ToDisplayString() == RavenIndexesNamespace
                && baseType.TypeArguments[0] is INamedTypeSymbol collection)
            {
                return collection;
            }
        }

        return null;
    }

    /// <summary>
    /// A location safe to report at: in source and not in a generated file. Never <c>Location.None</c>
    /// or a generated location — the generated-code filter drops those without a trace.
    /// </summary>
    private static Location? HandWrittenLocation(INamedTypeSymbol index)
        => index.Locations.FirstOrDefault(l =>
            l.IsInSource && !l.SourceTree!.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase));
}
