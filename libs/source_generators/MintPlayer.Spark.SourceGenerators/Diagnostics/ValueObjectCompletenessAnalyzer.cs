using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Finds embedded types the model reaches but nobody marked <c>[ValueObject]</c>, by walking the
/// object graph from the application's <c>SparkContext</c>.
/// </summary>
/// <remarks>
/// This is the completeness half of the marker design. The generator in
/// <c>MintPlayer.Spark.LibraryGenerators</c> answers "is this type a value object" — the author says
/// so, and it emits a key. Nothing there can answer "did you forget one", because an entity library
/// does not know which of its types the model actually reaches.
/// <para>
/// ⚠️ <b>The same walk failed as a generator and works here.</b> A generator may only add a
/// <c>partial</c> half to a type in its own compilation, and every Spark context lives in the
/// application project while every entity lives in a library — so a context-rooted generator could
/// never emit where it needed to. An analyzer emits nothing, so the constraint does not apply. The
/// substitute roots that were tried instead all answered a different question: rooting at
/// <c>[GenerateIndex]</c> flagged four types with no model file, one of them a row per line of
/// source code, while missing two persistent objects that carry no index. The context roots the
/// <em>model</em>, which is the question.
/// </para>
/// <para>
/// ⚠️ <b>It deliberately does not check <c>partial</c> — as a division of responsibility, not
/// because it could not.</b> Partiality belongs to SPARK016, in the compilation that owns the
/// syntax. An analyzer reads it perfectly well for any source-declared symbol; the guarded idiom is
/// <c>declarations.Length > 0 &amp;&amp; declarations.All(d => d.Modifiers.Any(SyntaxKind.PartialKeyword))</c>,
/// already used at <c>ValueObjectKeyGenerator.cs:85-86</c>. Drop the <c>Length > 0</c> guard and
/// <c>All()</c> over an empty <c>DeclaringSyntaxReferences</c> is vacuously <see langword="true"/>
/// for every metadata type — a bug this file once generalised into an impossibility. Only a true
/// metadata symbol is genuinely opaque, and there nothing is fixable anyway.
/// <para>
/// The <b>code fix</b> for SPARK017 consequently adds <c>partial</c> as well as the attribute, in
/// one edit: clearing SPARK017 only to raise SPARK016 on the next build would leave the author
/// worse off than no fix at all.
/// </para>
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class ValueObjectCompletenessAnalyzer : DiagnosticAnalyzer
{
    private const string SparkContextFullName = "MintPlayer.Spark.SparkContext";
    private const string RavenQueryableMetadataName = "Raven.Client.Documents.Linq.IRavenQueryable`1";
    private const string ValueObjectAttributeFullName = "MintPlayer.Spark.Abstractions.ValueObjectAttribute";

    /// <summary>
    /// Diagnostic property carrying the offending type's metadata name, so the code fix can resolve
    /// the symbol instead of guessing from the location — which may point at the context property
    /// in another file, or at nothing at all.
    /// </summary>
    public const string OffendingTypeProperty = "SparkOffendingType";

    /// <summary>
    /// Namespace-qualified, no <c>global::</c> — the shape <see cref="Compilation.GetTypeByMetadataName"/>
    /// expects on the way back in.
    /// </summary>
    private static readonly SymbolDisplayFormat MetadataNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => [MissingValueObjectRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // ⚠️ Per-symbol, NOT RegisterCompilationAction — and the difference is the whole code fix.
        //
        // A compilation-end action's diagnostics reach the Error List on build but are not live
        // document diagnostics in Visual Studio, and the light bulb only offers fixes for live
        // ones. Measured in the IDE: SPARK017 squiggled on the context property exactly as
        // intended and no fix was ever offered, while SPARK016 — emitted from a generator, of all
        // things — offered its fix without trouble.
        //
        // Nothing was lost by moving. The roots of the walk are the context's own IRavenQueryable
        // properties, so a symbol action on the SparkContext subclass has everything the
        // compilation-wide version had; the old comment here claimed otherwise and was simply
        // wrong about its own analyzer.
        context.RegisterSymbolAction(AnalyzeContextType, SymbolKind.NamedType);
    }

    /// <summary>
    /// Walks one <c>SparkContext</c> subclass. Non-context types return immediately.
    /// </summary>
    /// <remarks>
    /// Two contexts in one project that both reach the same unmarked type each report it, against
    /// their own property. That is a duplicate only in the sense that one edit clears both; each
    /// diagnostic is true of the context it names, and the alternative — suppressing the second —
    /// would leave a context silently unreported the moment the first one is fixed or deleted.
    /// </remarks>
    private static void AnalyzeContextType(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } contextType)
            return;

        var compilation = context.Compilation;

        var contextBase = compilation.GetTypeByMetadataName(SparkContextFullName);
        if (contextBase is null || !InheritsFrom(contextType, contextBase))
            return;

        var queryable = compilation.GetTypeByMetadataName(RavenQueryableMetadataName);
        if (queryable is null)
            return;

        var roots = RootsOf(contextType, queryable, context.CancellationToken);
        if (roots.Count == 0)
            return;

        foreach (var (offender, seedLocation) in FindUnmarked(roots, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingValueObjectRule,
                LocationFor(compilation, offender, seedLocation),
                // The code fix cannot re-derive which type this was from a message, and the
                // location may point at the context property rather than the type itself. Carry
                // the metadata name so the fix can look the symbol up directly.
                properties: ImmutableDictionary<string, string?>.Empty
                    .Add(OffendingTypeProperty, offender.ToDisplayString(MetadataNameFormat)),
                offender.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }
    }

    /// <summary>
    /// Where to point the diagnostic: the <c>SparkContext</c> property that reaches the offending
    /// type — always, even when this compilation declares that type itself.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reporting at the type's own location across a project boundary loses the diagnostic
    /// entirely, and this was measured, not assumed.</b> Roslyn's analyzer driver discards any
    /// diagnostic whose location lives in a syntax tree the analyzed compilation does not contain.
    /// A referenced project in a loaded IDE solution is a <c>CompilationReference</c>, so the
    /// offending type *does* have a source location — one belonging to the other project's tree —
    /// and the driver dropped it: a two-project fixture reported <b>zero</b> diagnostics where the
    /// identical single-project fixture reported one.
    /// <para>
    /// The effect on shipped behaviour was that a missing <c>[ValueObject]</c> on a type in an
    /// entity library was reported by <c>dotnet build</c> (where the type is metadata, the location
    /// is <see cref="Location.None"/>, and nothing is dropped) but was <b>invisible in the IDE</b>,
    /// which is the one place a developer is looking. Falling back to the context property keeps
    /// the diagnostic inside this compilation, so it survives.
    /// </para>
    /// <para>
    /// ⚠️ <b>Pointing at the offending type is wrong even when the type is in this compilation.</b>
    /// The walk runs as a symbol action on the context, so Roslyn attributes its diagnostics to the
    /// context's document; a location in a different file is filtered out of that document's
    /// semantic diagnostics and never becomes live, which is the same disappearance as the
    /// cross-project case with a subtler cause. Measured by
    /// <c>SPARK017_is_reported_by_a_document_scoped_action</c>, which failed on exactly this before
    /// the location was made unconditional.
    /// </para>
    /// <para>
    /// This is the rule <c>InterfaceImplementationAnalyzer</c> (INTF001) follows and the reason its
    /// light bulb works: <b>report at a location the analyzed symbol owns, and let only the fix
    /// travel.</b> The cost is that the squiggle sits on the context property rather than the type,
    /// so the message names the fully-qualified type and
    /// <see cref="OffendingTypeProperty"/> carries it to the fix, which edits whichever project
    /// declares it. See <c>ValueObjectCompletenessCodeFixProvider</c>.
    /// </para>
    /// </remarks>
    private static Location? LocationFor(
        Compilation compilation, INamedTypeSymbol offender, Location? seedLocation)
    {
        if (seedLocation is not null)
            return seedLocation;

        // No seed only when the context property itself is not in source, which a symbol action on
        // a source-declared context should never see. Fall back to the type's own declaration if
        // this compilation owns it, and to no location at all otherwise.
        foreach (var location in offender.Locations)
        {
            if (location.IsInSource && location.SourceTree is { } tree && compilation.ContainsSyntaxTree(tree))
                return location;
        }

        return null;
    }

    /// <summary>
    /// The document types the model starts from: the generic arguments of every
    /// <c>IRavenQueryable&lt;T&gt;</c> property on a <c>SparkContext</c> subclass.
    /// </summary>
    /// <remarks>
    /// Each root carries the location of the property that declared it. That property lives on the
    /// context, i.e. in *this* compilation, which makes it the one place a diagnostic about a type
    /// from a referenced project can be reported without being discarded — see
    /// <see cref="LocationFor"/>.
    /// </remarks>
    private static List<(INamedTypeSymbol Root, Location? SeedLocation)> RootsOf(
        INamedTypeSymbol contextType, INamedTypeSymbol queryable,
        System.Threading.CancellationToken ct)
    {
        var roots = new List<(INamedTypeSymbol, Location?)>();

        foreach (var property in contextType.GetMembers().OfType<IPropertySymbol>())
        {
            ct.ThrowIfCancellationRequested();

            if (property.Type is not INamedTypeSymbol { IsGenericType: true } propertyType)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, queryable))
                continue;
            if (propertyType.TypeArguments.FirstOrDefault() is INamedTypeSymbol root)
                roots.Add((root, property.Locations.FirstOrDefault(l => l.IsInSource)));
        }

        return roots;
    }

    /// <summary>
    /// Every unmarked type the model embeds <b>as a collection element</b> below
    /// <paramref name="roots"/>, deduplicated and ordered so the diagnostic set is stable.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A single nested object needs no key, and is deliberately not reported.</b> A key exists
    /// to match rows of a collection across a save; a single-valued member is matched by its
    /// property name, which cannot go missing or be reordered. Requiring the marker there would flag
    /// seven types across three apps — <c>CoverageSummary</c>, <c>GateSettings</c> and friends — none
    /// of which is ever a row of anything.
    /// <para>
    /// Single nested objects are still <em>walked</em>, because a collection two levels down is just
    /// as much part of the model as one at the top.
    /// </para>
    /// <para>
    /// The roots themselves are documents, so they are visited and never reported; they seed the
    /// visited set for that reason.
    /// </para>
    /// </remarks>
    private static IEnumerable<(INamedTypeSymbol Offender, Location? SeedLocation)> FindUnmarked(
        List<(INamedTypeSymbol Root, Location? SeedLocation)> roots, System.Threading.CancellationToken ct)
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<(INamedTypeSymbol Type, Location? SeedLocation)>();
        var unmarked = new Dictionary<string, (INamedTypeSymbol Offender, Location? SeedLocation)>(
            System.StringComparer.Ordinal);

        foreach (var (root, seedLocation) in roots)
        {
            if (visited.Add(root))
                queue.Enqueue((root, seedLocation));
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            // The seed travels with the walk: whichever context property led here is the one that
            // can carry a diagnostic about a type this compilation does not own.
            var (owner, seed) = queue.Dequeue();

            foreach (var property in owner.GetMembers().OfType<IPropertySymbol>())
            {
                if (!property.IsSparkModelProperty())
                    continue;

                // A [Reference] property points at documents by id; its target is a document of its
                // own, reachable from the context in its own right, and never embedded here.
                if (property.IsReferenceProperty())
                    continue;

                if (EmbeddedTypeOf(property.Type, out var isCollectionElement) is not { } embedded)
                    continue;

                // Report before the visited check: the same type can be a single member in one place
                // and a row in another, and it is the row usage that decides.
                if (isCollectionElement && !IsMarked(embedded))
                    unmarked[embedded.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)] = (embedded, seed);

                if (!visited.Add(embedded))
                    continue;

                // Walk it either way: an unmarked type's own children are still part of the model,
                // and reporting a whole subtree at once beats one type per build.
                queue.Enqueue((embedded, seed));
            }
        }

        return unmarked.OrderBy(x => x.Key, System.StringComparer.Ordinal).Select(x => x.Value);
    }

    /// <summary>
    /// The complex type a property embeds — itself, or its collection element — or
    /// <see langword="null"/> when the property persists as a scalar.
    /// <paramref name="isCollectionElement"/> reports which of the two, because only a row of a
    /// collection needs a key.
    /// </summary>
    private static INamedTypeSymbol? EmbeddedTypeOf(ITypeSymbol propertyType, out bool isCollectionElement)
    {
        isCollectionElement = false;
        var current = propertyType.UnwrapNullable();

        // ⚠️ Dictionaries first, and the order is load-bearing: a Dictionary<K,V> satisfies
        // IEnumerable<KeyValuePair<K,V>>, so element extraction would yield a BCL struct we do not
        // own, and the runtime does not model such a property as embedded either.
        if (current.IsSparkDictionaryLike())
            return null;

        if (current.GetCollectionElementType() is { } element)
        {
            isCollectionElement = true;
            current = element.UnwrapNullable();
        }

        return current is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named
            && named.SpecialType == SpecialType.None
            && !named.IsSparkScalarLike()
            && named.GetMembers().OfType<IPropertySymbol>().Any(p => p.DeclaredAccessibility == Accessibility.Public)
            ? named
            : null;
    }

    private static bool IsMarked(INamedTypeSymbol type)
        => type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == ValueObjectAttributeFullName);

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol candidateBase)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, candidateBase))
                return true;
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(
        INamespaceSymbol ns, System.Threading.CancellationToken ct)
    {
        foreach (var member in ns.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in AllTypes(nested, ct))
                        yield return type;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nestedType in type.GetTypeMembers())
                        yield return nestedType;
                    break;
            }
        }
    }
}
