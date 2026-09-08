using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Gives every embedded collection element a stable per-row key.
/// </summary>
/// <remarks>
/// A type is a value object when some type <em>in this compilation</em> has a persisted property
/// whose collection element type is that type, and the element type is complex. There is no
/// attribute to apply: the set is computed, so it cannot fall out of step with the model by someone
/// forgetting to annotate a class.
/// <para>
/// ⚠️ <b>Deliberately no context root.</b> The obvious design walks from the Spark context's
/// <c>IRavenQueryable&lt;T&gt;</c> properties. It cannot work here: a generator may only add a
/// <c>partial</c> half to a type in its own compilation, every context lives in the app project, and
/// every entity lives in a class library — so a context-rooted walk finds nothing it is allowed to
/// emit for. A root is unnecessary anyway; it exists to avoid walking non-model types, and an entity
/// library holds nothing else.
/// </para>
/// <para>
/// Recursion falls out for free. The pass visits every type rather than descending from a root, so a
/// value object's own collections are found by the same sweep — and no cycle guard is needed,
/// because nothing recurses.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class ValueObjectKeyGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        var valueObjectsProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is TypeDeclarationSyntax { Members.Count: > 0 }
                    and (ClassDeclarationSyntax or RecordDeclarationSyntax),
                transform: static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is { Length: > 0 })
            .SelectMany(static (x, ct) => x!)
            .WithComparer(ComparerRegistry.For<ValueObjectInfo>())
            .Collect()
            .Select(static (found, ct) => Distinct(found))
            .WithComparer(ComparerRegistry.For<ImmutableArray<ValueObjectInfo>>());

        var sourceProvider = valueObjectsProvider
            .Join(settingsProvider)
            .Select(static Producer (p, ct) => new ValueObjectKeyProducer(p.Item1, p.Item2.RootNamespace ?? "GeneratedCode"));

        var diagnosticsProvider = valueObjectsProvider
            .Select(static IDiagnosticReporter (valueObjects, ct) => new ValueObjectKeyReporter(valueObjects));

        context.ProduceCode(sourceProvider);

        // Both calls, and the second is not optional. ProduceCode alone emits for the types it can
        // and says nothing about the rest, so a type that cannot be keyed is skipped in silence —
        // which is precisely the hole SPARK015 exists to close.
        context.ReportDiagnostics(diagnosticsProvider);
    }

    /// <summary>
    /// One owner type in, the value objects it introduces out. Returns the <em>element</em> types,
    /// not the owner — the owner is only the evidence that the element is a collection member.
    /// </summary>
    private static ValueObjectInfo[]? Describe(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not TypeDeclarationSyntax declaration)
            return null;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol owner)
            return null;

        // A document root is the entry point into the model. Persisted types that are not roots
        // and are not reachable from one are outside the model entirely, and keying their
        // collections would add a guid to rows nothing edits — see Reachable.
        var ownerIsRoot = owner.GetAttributes().Any(static a =>
            a.AttributeClass?.ToDisplayString() == "MintPlayer.Spark.Abstractions.GenerateIndexAttribute");

        List<ValueObjectInfo>? found = null;

        foreach (var member in owner.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is not IPropertySymbol property || !property.IsSparkModelProperty())
                continue;

            // A [Reference] collection points at documents by id; its element is never embedded.
            // Mirrors the runtime, where the reference attribute wins outright over the shape.
            if (property.IsSparkReference())
                continue;

            // ⚠️ Dictionaries first, and this ordering is load-bearing. A Dictionary<K,V> satisfies
            // IEnumerable<KeyValuePair<K,V>>, so element extraction yields a BCL struct we do not own
            // — and the runtime does not model such a property as AsDetail either. Unwrapping one
            // would invent a value object out of a dictionary's value type.
            if (property.Type.IsSparkDictionaryLike())
                continue;

            var element = property.Type.GetCollectionElementType();
            if (element is not INamedTypeSymbol { } elementType || !IsValueObjectCandidate(elementType))
                continue;

            // Only types declared in this compilation: a partial half cannot be added to a type
            // whose source we do not own. An element type from another assembly is that assembly's
            // job, and its own copy of this generator handles it.
            if (!elementType.DeclaringSyntaxReferences.Any())
                continue;

            (found ??= []).Add(Capture(elementType, owner, ownerIsRoot, ct));
        }

        return found?.ToArray();
    }

    private static ValueObjectInfo Capture(
        INamedTypeSymbol type, INamedTypeSymbol owner, bool ownerIsRoot, CancellationToken ct)
    {
        var declarations = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<TypeDeclarationSyntax>()
            .ToArray();

        return new ValueObjectInfo
        {
            FullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name = type.Name,
            PathSpec = type.GetPathSpec(ct),
            IsPartial = declarations.Length > 0
                && declarations.All(d => d.Modifiers.Any(SyntaxKind.PartialKeyword)),
            DeclaresOwnId = type.GetMembers("Id").OfType<IPropertySymbol>().Any(),
            Location = declarations.FirstOrDefault()?.Identifier.GetLocation().AsKey(),
            OwnerFullyQualifiedName = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            OwnerIsRoot = ownerIsRoot,
        };
    }

    /// <summary>
    /// Mirrors the runtime's <c>SparkModelShape.IsComplexType</c>, which is the definition of "this
    /// is an embedded object" everywhere else.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not <c>IsComplexForIndex</c>. That one deliberately disagrees — structs and dictionaries
    /// are complex <em>for indexing</em> — and following it here would key types the model does not
    /// consider embedded at all.
    /// </remarks>
    private static bool IsValueObjectCandidate(INamedTypeSymbol type)
        => type is { TypeKind: TypeKind.Class, IsAbstract: false }
        && type.SpecialType == SpecialType.None
        && !type.IsSparkScalarLike()
        && type.GetMembers().OfType<IPropertySymbol>().Any(p => p.DeclaredAccessibility == Accessibility.Public);

    /// <summary>
    /// The value objects actually part of the model: those hanging off a document root, and those
    /// hanging off one of those, transitively. One entry per type however many collections reach it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The root filter is not tidiness, it is the difference between keying a handful of editable
    /// rows and keying every line of every coverage report. An entity library holds persisted types
    /// that are not part of the Spark model at all, and those are frequently the highest-volume
    /// documents in the application.
    /// <para>
    /// A model type that is <em>not</em> reachable from a <c>[GenerateIndex]</c> root — a persistent
    /// object that happens to carry no index — is missed here on purpose rather than by oversight:
    /// widening the root set to catch it would drag the non-model types back in. The startup gate
    /// catches that case instead, where the model itself is loaded and can be compared against what
    /// was registered.
    /// </para>
    /// </remarks>
    private static ImmutableArray<ValueObjectInfo> Distinct(IEnumerable<ValueObjectInfo> found)
    {
        var edges = found.ToArray();

        var reachable = new HashSet<string>(
            edges.Where(static e => e.OwnerIsRoot).Select(static e => e.OwnerFullyQualifiedName),
            StringComparer.Ordinal);

        // Fixed point rather than recursion: an owner becomes reachable once something reaches it,
        // which can enable edges already passed over. Bounded by the edge count, so a cycle in the
        // type graph terminates instead of spinning.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var edge in edges)
            {
                if (reachable.Contains(edge.OwnerFullyQualifiedName)
                    && reachable.Add(edge.FullyQualifiedName))
                {
                    grew = true;
                }
            }
        }

        var seen = new Dictionary<string, ValueObjectInfo>(StringComparer.Ordinal);
        foreach (var info in edges)
        {
            if (reachable.Contains(info.FullyQualifiedName) && !seen.ContainsKey(info.FullyQualifiedName))
                seen.Add(info.FullyQualifiedName, info);
        }

        return [.. seen.Values.OrderBy(static x => x.FullyQualifiedName, StringComparer.Ordinal)];
    }
}
