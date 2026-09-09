using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Gives every <c>[ValueObject]</c> type a stable per-row key, and registers the set with the runtime.
/// </summary>
/// <remarks>
/// A type is a value object because it says so. The set is never inferred.
/// <para>
/// ⚠️ <b>The computed design was built and abandoned — do not re-propose it.</b> Deriving the set by
/// walking the type graph fails on a structural constraint: a generator may only add a
/// <c>partial</c> half to a type in <em>its own</em> compilation, and every Spark context lives in
/// the application project while every entity lives in a library the application references, so a
/// context-rooted walk can never start where it must emit. Every substitute root fared worse.
/// Rooting at <c>[GenerateIndex]</c> keyed four types with no model file at all — one of them a row
/// per line of code — while still missing two persistent objects that carry no index.
/// </para>
/// <para>
/// A computed set only beats a marker when the computation can express the question. Here it could
/// not: each approximation quietly substituted "is this indexed?" or "is this reachable?" for "is
/// this a value object?".
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class ValueObjectKeyGenerator : IncrementalGenerator
{
    private const string ValueObjectAttribute = "MintPlayer.Spark.Abstractions.ValueObjectAttribute";
    private const string ValueKeyAttribute = "MintPlayer.Spark.Abstractions.ValueKeyAttribute";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        var valueObjectsProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ValueObjectAttribute,
                predicate: static (node, ct) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, ct) => x!)
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
        // which is precisely the hole SPARK016 exists to close. Wiring only the first is how four
        // types were dropped without a word on this generator's very first run.
        context.ReportDiagnostics(diagnosticsProvider);
    }

    private static ValueObjectInfo? Describe(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

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
            ExistingKeyProperty = FindDeclaredKey(type),
            Location = declarations.FirstOrDefault()?.Identifier.GetLocation().AsKey(),
        };
    }

    /// <summary>
    /// The property the author marked <c>[ValueKey]</c>, if any.
    /// </summary>
    /// <remarks>
    /// The key need only be stable and unique within its collection — it need not be a Guid, and it
    /// need not be called <c>Id</c>. Two of this workspace's own keys are neither: a GitHub
    /// single-select option id assigned from the API, and a value derived from an event type during
    /// save.
    /// </remarks>
    private static string? FindDeclaredKey(INamedTypeSymbol type)
        => type.GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static p => p.GetAttributes().Any(static a =>
                a.AttributeClass?.ToDisplayString() == ValueKeyAttribute))
            ?.Name;

    /// <summary>
    /// One entry per type, ordered, so the emitted file is stable across runs.
    /// </summary>
    /// <remarks>
    /// A dedupe rather than a filter. With the marker as the source of truth there is no set to
    /// narrow — partial declarations across several files are the only way the same type arrives
    /// twice, and <c>ForAttributeWithMetadataName</c> reports the attributed declaration, so even
    /// that is rare.
    /// </remarks>
    private static ImmutableArray<ValueObjectInfo> Distinct(IEnumerable<ValueObjectInfo> found)
    {
        var seen = new Dictionary<string, ValueObjectInfo>(StringComparer.Ordinal);
        foreach (var info in found)
        {
            if (!seen.ContainsKey(info.FullyQualifiedName))
                seen.Add(info.FullyQualifiedName, info);
        }

        return [.. seen.Values.OrderBy(static x => x.FullyQualifiedName, StringComparer.Ordinal)];
    }
}
