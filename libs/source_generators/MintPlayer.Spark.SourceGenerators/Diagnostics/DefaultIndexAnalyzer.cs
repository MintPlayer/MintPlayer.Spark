using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using MintPlayer.Spark.SourceGenerators.Naming;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// <c>[DefaultIndex(params Type[] entityTypes)]</c> at compile time (<c>docs/default_index_PRD.md</c>, D1–D5):
/// one claim per entity type (SPARK009), and the shapes a marker may take (SPARK051–055).
/// <para>
/// The authoritative check is the index catalog's freeze-time validation — it alone sees indexes
/// contributed across assemblies via <c>AddIndexesFrom(...)</c>, and it cannot be switched off. This
/// analyzer is the compile-time mirror for the common case where the claims live in the app project,
/// so the author gets a squiggle instead of a startup failure. The one check only this analyzer makes is
/// SPARK053: a multi-map's <c>AddMap&lt;X&gt;</c> calls are constructor bodies, which a referenced assembly's
/// metadata does not contain, so startup cannot see them.
/// </para>
/// <para>
/// A <c>[GenerateIndex]</c> entity is <b>not</b> a claim here any more (D5): its generated index yields to a
/// hand-written claim and is emitted without the marker, so the two never compete. When the generated tree
/// is analyzed, an unclaimed generated index shows up as an ordinary single-map claim. Reports anchor only on
/// hand-written declarations: the generated-code filter drops diagnostics by location, and a generated location
/// is unfixable anyway.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DefaultIndexAnalyzer : DiagnosticAnalyzer
{
    private const string SparkMultiMapIndexCreationTaskFullName = "MintPlayer.Spark.SparkMultiMapIndexCreationTask<TReduceResult>";

    internal static readonly DiagnosticDescriptor DuplicateDefaultIndexRule = new(
        id: "SPARK009",
        title: "Multiple [DefaultIndex] claims for one entity type",
        messageFormat: "'{0}' and '{1}' both claim [DefaultIndex] for entity type '{2}'; an entity type has exactly one default index. Remove one claim (a [GenerateIndex] entity's generated index yields to a hand-written claim by itself)",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>D1: a single-map is the default of the type it maps, and of nothing else.</summary>
    internal static readonly DiagnosticDescriptor SingleMapTypeMismatchRule = new(
        id: "SPARK051",
        title: "[DefaultIndex] on a single-map index names a type other than the mapped one",
        messageFormat: "'{0}' maps '{1}', so [DefaultIndex] can only claim '{1}', but it names {2}. Remove the argument (a bare marker claims '{1}')",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>D1: a multi-map names no collection in its type arguments, so its claims must be listed.</summary>
    internal static readonly DiagnosticDescriptor MultiMapWithoutTypesRule = new(
        id: "SPARK052",
        title: "[DefaultIndex] on a multi-map index lists no entity types",
        messageFormat: "'{0}' is a multi-map index, so [DefaultIndex] must list the entity types it is the default of, e.g. [DefaultIndex(typeof(Car), typeof(Bike))]",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// D1: same-project <c>AddMap</c> inspection validates the listed types. A listed type the index never maps
    /// would be "defaulted" to an index holding none of its documents: an empty list page, and no error anywhere.
    /// </summary>
    internal static readonly DiagnosticDescriptor ListedTypeNotMappedRule = new(
        id: "SPARK053",
        title: "[DefaultIndex] lists a type the multi-map index does not map",
        messageFormat: "'{0}' claims '{1}' with [DefaultIndex], but none of its maps is AddMap<{2}>(…). Add the map or remove the type from the marker",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>
    /// PRD §7, SP1: a JavaScript index cannot derive from <c>SparkMultiMapIndexCreationTask</c>, so Spark can neither
    /// read its mapped type nor inject the collection discriminator. The catalog refuses it at startup.
    /// </summary>
    internal static readonly DiagnosticDescriptor JavaScriptIndexRule = new(
        id: "SPARK054",
        title: "[DefaultIndex] on a JavaScript index",
        messageFormat: "'{0}' is a JavaScript index, which cannot be a default index: Spark can neither tell which entity type it serves nor narrow its rows to one collection. Mark a C# index instead",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// D4: without the collection discriminator <c>SparkMultiMapIndexCreationTask</c> injects, a multi-map default
    /// returns every mapped collection's rows as <c>T</c> (and bypasses those collections' row policies).
    /// </summary>
    internal static readonly DiagnosticDescriptor MultiMapNotSparkRule = new(
        id: "SPARK055",
        title: "A multi-map default index must derive from SparkMultiMapIndexCreationTask",
        messageFormat: "'{0}' is a multi-map default index but does not derive from SparkMultiMapIndexCreationTask, so Spark cannot narrow its rows to the queried type's collection. Derive from SparkMultiMapIndexCreationTask<TReduceResult>",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(
            DuplicateDefaultIndexRule,
            SingleMapTypeMismatchRule,
            MultiMapWithoutTypesRule,
            ListedTypeNotMappedRule,
            JavaScriptIndexRule,
            MultiMapNotSparkRule);

    private readonly struct DefaultClaim
    {
        public DefaultClaim(string indexName, INamedTypeSymbol entityType, Location? location)
        {
            IndexName = indexName;
            EntityType = entityType;
            Location = location;
        }

        public string IndexName { get; }
        public INamedTypeSymbol EntityType { get; }
        public Location? Location { get; }
    }

    /// <summary>A multi-map's listed types, checked against its <c>AddMap</c> calls once every operation was seen.</summary>
    private readonly struct ListedTypes
    {
        public ListedTypes(INamedTypeSymbol index, List<INamedTypeSymbol> types, Location location)
        {
            Index = index;
            Types = types;
            Location = location;
        }

        public INamedTypeSymbol Index { get; }
        public List<INamedTypeSymbol> Types { get; }
        public Location Location { get; }
    }

    /// <summary>One <c>AddMap&lt;X&gt;</c> / <c>AddMapForAll&lt;X&gt;</c> call, keyed by the type whose code makes it.</summary>
    private readonly struct MapCall
    {
        public MapCall(INamedTypeSymbol declaringType, ITypeSymbol mappedType, bool forAll)
        {
            DeclaringType = declaringType;
            MappedType = mappedType;
            ForAll = forAll;
        }

        public INamedTypeSymbol DeclaringType { get; }
        public ITypeSymbol MappedType { get; }
        public bool ForAll { get; }
    }

    public override void Initialize(AnalysisContext context)
    {
        // Analyze (not None): a generated [DefaultIndex] index should still enter the walk when the host
        // analyzes generated trees, so a hand-written claim over the same type is reported against it.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var claims = new ConcurrentBag<DefaultClaim>();
            var listed = new ConcurrentBag<ListedTypes>();
            var mapCalls = new ConcurrentBag<MapCall>();

            compilationStart.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.IsAbstract) return;
                if (DefaultIndexClaims.Marker(type) is not { } marker) return;

                var location = HandWrittenLocation(type);
                var markerLocation = HandWrittenMarkerLocation(marker, symbolContext.CancellationToken) ?? location;

                if (DefaultIndexClaims.IsJavaScript(type))
                {
                    if (location is not null)
                        symbolContext.ReportDiagnostic(Diagnostic.Create(JavaScriptIndexRule, location, type.Name));
                    return;
                }

                var explicitTypes = DefaultIndexClaims.ExplicitTypes(marker);

                switch (RavenIndexHierarchy.Classify(type, out var mapped))
                {
                    case RavenIndexKind.Map when mapped is not null:
                        if (explicitTypes.Count > 0
                            && !(explicitTypes.Count == 1 && SymbolEqualityComparer.Default.Equals(explicitTypes[0], mapped))
                            && markerLocation is not null)
                        {
                            symbolContext.ReportDiagnostic(Diagnostic.Create(
                                SingleMapTypeMismatchRule, markerLocation,
                                type.Name, mapped.ToDisplayString(),
                                string.Join(", ", explicitTypes.Select(t => $"'{t.ToDisplayString()}'"))));
                        }

                        // The catalog refuses a wrong argument at startup but would elect the mapped type, so the
                        // clash check counts that.
                        claims.Add(new DefaultClaim(type.Name, mapped, location));
                        break;

                    case RavenIndexKind.MultiMap:
                        if (explicitTypes.Count == 0)
                        {
                            if (markerLocation is not null)
                                symbolContext.ReportDiagnostic(Diagnostic.Create(MultiMapWithoutTypesRule, markerLocation, type.Name));
                            break;
                        }

                        if (!RavenIndexHierarchy.DerivesFrom(type, SparkMultiMapIndexCreationTaskFullName) && location is not null)
                            symbolContext.ReportDiagnostic(Diagnostic.Create(MultiMapNotSparkRule, location, type.Name));

                        foreach (var entityType in DefaultIndexClaims.ClaimedTypes(type, marker))
                            claims.Add(new DefaultClaim(type.Name, entityType, location));

                        if (markerLocation is not null)
                            listed.Add(new ListedTypes(type, explicitTypes, markerLocation));
                        break;
                }
            }, SymbolKind.NamedType);

            // Every AddMap<X>(…) in the compilation, resolved through the semantic model: the explicit form and the
            // inferred one (AddMap((IEnumerable<Car> cars) => …)) bind to the same generic method (PRD §7, SP2).
            compilationStart.RegisterOperationAction(operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                var method = invocation.TargetMethod;
                if (method.Name is not ("AddMap" or "AddMapForAll") || method.TypeArguments.Length != 1) return;
                if (method.ContainingType?.OriginalDefinition.ContainingNamespace?.ToDisplayString()
                        != RavenIndexHierarchy.RavenIndexesNamespace) return;
                if (operationContext.ContainingSymbol.ContainingType is not { } declaringType) return;

                mapCalls.Add(new MapCall(declaringType, method.TypeArguments[0], method.Name == "AddMapForAll"));
            }, OperationKind.Invocation);

            compilationStart.RegisterCompilationEndAction(endContext =>
            {
                ReportUnmappedListedTypes(endContext, listed, mapCalls);

                foreach (var group in claims
                    .GroupBy(c => (ITypeSymbol)c.EntityType, SymbolEqualityComparer.Default))
                {
                    var distinct = group
                        .GroupBy(c => c.IndexName, System.StringComparer.Ordinal)
                        .Select(g => new DefaultClaim(g.Key, g.First().EntityType, g.Select(c => c.Location).FirstOrDefault(l => l is not null)))
                        .OrderBy(c => c.IndexName, System.StringComparer.Ordinal)
                        .ToList();
                    if (distinct.Count < 2) continue;

                    foreach (var claim in distinct)
                    {
                        if (claim.Location is null) continue;

                        var other = distinct.First(c => c.IndexName != claim.IndexName);
                        endContext.ReportDiagnostic(Diagnostic.Create(
                            DuplicateDefaultIndexRule, claim.Location,
                            claim.IndexName, other.IndexName, group.Key!.ToDisplayString()));
                    }
                }
            });
        });
    }

    /// <summary>
    /// SPARK053: a listed type that no <c>AddMap&lt;X&gt;</c> of the index (or of a base class it inherits its maps from)
    /// maps. <c>AddMapForAll&lt;TBase&gt;</c> covers every listed type deriving from or implementing <c>TBase</c>.
    /// </summary>
    private static void ReportUnmappedListedTypes(
        CompilationAnalysisContext context, IEnumerable<ListedTypes> listed, IEnumerable<MapCall> mapCalls)
    {
        var callsByType = mapCalls
            .GroupBy(c => c.DeclaringType, SymbolEqualityComparer.Default)
            .ToDictionary(g => (INamedTypeSymbol)g.Key!, g => g.ToList(), SymbolEqualityComparer.Default);

        foreach (var entry in listed)
        {
            var calls = new List<MapCall>();
            for (var current = entry.Index; current is not null; current = current.BaseType)
            {
                if (callsByType.TryGetValue(current.OriginalDefinition, out var own)) calls.AddRange(own);
                else if (callsByType.TryGetValue(current, out own)) calls.AddRange(own);
            }

            foreach (var type in entry.Types.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
            {
                if (calls.Any(c => Maps(c, type))) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    ListedTypeNotMappedRule, entry.Location,
                    entry.Index.Name, type.ToDisplayString(), type.Name));
            }
        }
    }

    private static bool Maps(MapCall call, INamedTypeSymbol listedType)
    {
        if (SymbolEqualityComparer.Default.Equals(call.MappedType, listedType)) return true;
        if (!call.ForAll) return false;

        for (var current = listedType.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, call.MappedType)) return true;

        return listedType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, call.MappedType));
    }

    /// <summary>
    /// A location safe to report at: in source and not in a generated file. Never <c>Location.None</c>
    /// or a generated location — the generated-code filter drops those without a trace.
    /// </summary>
    private static Location? HandWrittenLocation(INamedTypeSymbol index)
        => index.Locations.FirstOrDefault(IsHandWritten);

    /// <summary>The marker's own attribute syntax, when it is hand-written, so a wrong argument squiggles where it is.</summary>
    private static Location? HandWrittenMarkerLocation(AttributeData marker, System.Threading.CancellationToken ct)
        => marker.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation() is { } location && IsHandWritten(location)
            ? location
            : null;

    private static bool IsHandWritten(Location location)
        => location.IsInSource
            && !location.SourceTree!.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase);
}
