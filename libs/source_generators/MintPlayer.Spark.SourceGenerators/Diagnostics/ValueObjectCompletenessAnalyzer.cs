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
/// ⚠️ <b>It deliberately does not check <c>partial</c>.</b> That keyword is source-only and leaves no
/// trace in IL, so no metadata symbol can report it — and the obvious helper gets it silently wrong,
/// computing partial-ness as <c>All()</c> over an empty <c>DeclaringSyntaxReferences</c>, which is
/// <see langword="true"/> for every metadata type. Partiality belongs to SPARK016, in the
/// compilation that owns the syntax.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class ValueObjectCompletenessAnalyzer : DiagnosticAnalyzer
{
    private const string SparkContextFullName = "MintPlayer.Spark.SparkContext";
    private const string RavenQueryableMetadataName = "Raven.Client.Documents.Linq.IRavenQueryable`1";
    private const string ValueObjectAttributeFullName = "MintPlayer.Spark.Abstractions.ValueObjectAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => [MissingValueObjectRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Compilation-wide, not per-symbol: the question is about a graph, and the roots live in a
        // different file from almost everything the walk reaches.
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        var compilation = context.Compilation;

        // No context type in this compilation means this is a library, not an application, and the
        // roots are somewhere else. Say nothing rather than guess.
        var contextBase = compilation.GetTypeByMetadataName(SparkContextFullName);
        if (contextBase is null)
            return;

        var queryable = compilation.GetTypeByMetadataName(RavenQueryableMetadataName);
        if (queryable is null)
            return;

        var roots = FindRoots(compilation, contextBase, queryable, context.CancellationToken);
        if (roots.Count == 0)
            return;

        foreach (var offender in FindUnmarked(roots, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingValueObjectRule,
                // Null renders as Location.None. Under `dotnet build` that is the only option for a
                // type from a referenced assembly; in the IDE the source location is available and
                // used. Neither changes whether the build fails.
                offender.Locations.FirstOrDefault(l => l.IsInSource),
                offender.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }
    }

    /// <summary>
    /// The document types the model starts from: the generic arguments of every
    /// <c>IRavenQueryable&lt;T&gt;</c> property on a <c>SparkContext</c> subclass.
    /// </summary>
    private static List<INamedTypeSymbol> FindRoots(
        Compilation compilation, INamedTypeSymbol contextBase, INamedTypeSymbol queryable,
        System.Threading.CancellationToken ct)
    {
        var roots = new List<INamedTypeSymbol>();

        foreach (var contextType in AllTypes(compilation.Assembly.GlobalNamespace, ct))
        {
            if (!InheritsFrom(contextType, contextBase))
                continue;

            foreach (var property in contextType.GetMembers().OfType<IPropertySymbol>())
            {
                ct.ThrowIfCancellationRequested();

                if (property.Type is not INamedTypeSymbol { IsGenericType: true } propertyType)
                    continue;
                if (!SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, queryable))
                    continue;
                if (propertyType.TypeArguments.FirstOrDefault() is INamedTypeSymbol root)
                    roots.Add(root);
            }
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
    private static IEnumerable<INamedTypeSymbol> FindUnmarked(
        List<INamedTypeSymbol> roots, System.Threading.CancellationToken ct)
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();
        var unmarked = new Dictionary<string, INamedTypeSymbol>(System.StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (visited.Add(root))
                queue.Enqueue(root);
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var owner = queue.Dequeue();

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
                    unmarked[embedded.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)] = embedded;

                if (!visited.Add(embedded))
                    continue;

                // Walk it either way: an unmarked type's own children are still part of the model,
                // and reporting a whole subtree at once beats one type per build.
                queue.Enqueue(embedded);
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
