using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits, in the application, the index classes that libraries declare with <c>[SparkIndexMap]</c> (commerce X19,
/// reference-data Q-R6).
/// <para>
/// Owner rule: RavenDB indexes always live in the application. A library therefore ships no index class, only its
/// maps, as static <c>Expression&lt;Func&lt;IEnumerable&lt;TSource&gt;, IEnumerable&lt;TResult&gt;&gt;&gt;</c> members. A
/// generator cannot read a method body from metadata, but it does not need to: the emitted index calls the
/// library's member, and RavenDB translates that expression tree when it builds the definition. Every map naming one
/// index, from any number of libraries, becomes one <c>AddMap</c> of a single multi-map index.
/// </para>
/// <para>
/// Emitted only in an application (OutputKind Console/Windows) that references <c>MintPlayer.Spark</c>, for the same
/// reason as <see cref="GenerateIndexGenerator"/> (#388): a class library has no use for its own copy.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public class DeclaredIndexGenerator : IncrementalGenerator
{
    internal const string SparkIndexMapAttributeFullName = "MintPlayer.Spark.Abstractions.SparkIndexMapAttribute";
    private const string SearchAttributeFullName = "MintPlayer.Spark.Abstractions.SearchAttribute";
    private const string MultiMapBaseMetadataName = "MintPlayer.Spark.SparkMultiMapIndexCreationTask`1";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var sourceMapsProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SparkIndexMapAttributeFullName,
                predicate: static (node, _) => node is PropertyDeclarationSyntax or VariableDeclaratorSyntax,
                transform: static (ctx, ct) => ctx.Attributes
                    .Select(attribute => Describe(ctx.TargetSymbol, attribute, fromSource: true, ct))
                    .ToList())
            .SelectMany(static (maps, _) => maps)
            .Collect();

        var referencedMapsProvider = context.CompilationProvider
            .Select(static (compilation, ct) => ScanReferenced(compilation, ct));

        var emitsProvider = context.CompilationProvider
            .Select(static (compilation, _) =>
                compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication
                && compilation.GetTypeByMetadataName(MultiMapBaseMetadataName) is not null);

        var producerProvider = sourceMapsProvider
            .Combine(referencedMapsProvider)
            .Combine(emitsProvider)
            .Combine(settingsProvider)
            .Select(static Producer (providers, _) => new DeclaredIndexProducer(
                providers.Left.Left.Left.Concat(providers.Left.Left.Right.Maps).ToList(),
                providers.Left.Right,
                providers.Right.RootNamespace ?? "GeneratedCode"));

        context.ProduceCode(producerProvider);
        context.ReportDiagnostics(
            producerProvider.Select(static IDiagnosticReporter (producer, _) => (IDiagnosticReporter)producer));
    }

    /// <summary>The declaration one <c>[SparkIndexMap]</c> attribute makes, with <see cref="DeclaredIndexMapInfo.Error"/> set when it cannot be emitted.</summary>
    private static DeclaredIndexMapInfo Describe(ISymbol member, AttributeData attribute, bool fromSource, CancellationToken ct)
    {
        var indexName = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        var storeAllFields = attribute.NamedArguments
            .Any(a => a.Key == "StoreAllFields" && a.Value.Value is true);

        var info = new DeclaredIndexMapInfo
        {
            IndexName = indexName ?? string.Empty,
            MemberDisplay = $"{member.ContainingType?.ToDisplayString()}.{member.Name}",
            MemberAccess = $"{member.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{member.Name}",
            StoreAllFields = storeAllFields,
            Location = fromSource ? member.Locations.FirstOrDefault(l => l.IsInSource).AsKey() : null,
        };

        if (string.IsNullOrEmpty(indexName) || !SyntaxFacts.IsValidIdentifier(indexName)
            || SyntaxFacts.GetKeywordKind(indexName!) != SyntaxKind.None)
        {
            info.Error = "its index name is not a valid C# class name";
            return info;
        }

        if (!member.IsStatic)
        {
            info.Error = "it is not static";
            return info;
        }

        if (!IsReachable(member, fromSource))
        {
            info.Error = fromSource
                ? "it is private or protected, so the generated index cannot read it"
                : "it is not public on a public type, so the application cannot read it";
            return info;
        }

        ITypeSymbol? memberType = member switch
        {
            IPropertySymbol { GetMethod: not null, Parameters.Length: 0 } property => property.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };

        if (memberType is null)
        {
            info.Error = "it is not a readable property or field";
            return info;
        }

        if (!TryReadMapType(memberType, out var sourceType, out var resultType))
        {
            info.Error = "its type is not Expression<Func<IEnumerable<TSource>, IEnumerable<TResult>>>";
            return info;
        }

        ct.ThrowIfCancellationRequested();

        info.SourceTypeFullName = sourceType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        info.ResultTypeFullName = resultType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (resultType is INamedTypeSymbol namedResult)
        {
            var properties = namedResult.GetSparkProperties().ToList();
            info.SearchFields = properties
                .Where(p => p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == SearchAttributeFullName))
                .Select(p => p.Name)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToList();
            // Commerce X18, fuzzy by default: a searchable field (Spark's search is typo-tolerant on it) or an
            // explicit [Fuzzy] one puts the index on Lucene.
            info.UsesLucene = info.SearchFields.Count > 0 || properties.Any(GenerateIndexGenerator.IsFuzzy);
        }

        return info;
    }

    /// <summary>Reads <c>Expression&lt;Func&lt;IEnumerable&lt;TSource&gt;, IEnumerable&lt;TResult&gt;&gt;&gt;</c>.</summary>
    private static bool TryReadMapType(ITypeSymbol type, out ITypeSymbol? source, out ITypeSymbol? result)
    {
        source = null;
        result = null;

        // Compared by metadata name and namespace: a display string would depend on the format's variance option
        // (Func<in T, out TResult>).
        if (type is not INamedTypeSymbol { MetadataName: "Expression`1", TypeArguments.Length: 1 } expression
            || expression.ContainingNamespace?.ToDisplayString() != "System.Linq.Expressions")
            return false;

        if (expression.TypeArguments[0] is not INamedTypeSymbol { MetadataName: "Func`2" } func
            || func.ContainingNamespace?.ToDisplayString() != "System")
            return false;

        if (!IsEnumerableOf(func.TypeArguments[0], out source) || !IsEnumerableOf(func.TypeArguments[1], out result))
            return false;

        // An open type parameter cannot be named from the application's index class.
        return source!.TypeKind != TypeKind.TypeParameter && result!.TypeKind != TypeKind.TypeParameter;
    }

    private static bool IsEnumerableOf(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = null;
        if (type is not INamedTypeSymbol { TypeArguments.Length: 1 } named
            || named.OriginalDefinition.SpecialType != SpecialType.System_Collections_Generic_IEnumerable_T)
            return false;

        element = named.TypeArguments[0];
        return true;
    }

    /// <summary>
    /// Whether the application can read <paramref name="member"/>: in its own source anything but private or
    /// protected; from a library only a public member of public types.
    /// </summary>
    private static bool IsReachable(ISymbol member, bool fromSource)
    {
        for (ISymbol? current = member; current is not null and not INamespaceSymbol; current = current.ContainingType)
        {
            var accessibility = current.DeclaredAccessibility;
            if (fromSource
                ? accessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal
                : accessibility != Accessibility.Public)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Every <c>[SparkIndexMap]</c> declaration in a referenced assembly. Only assemblies that reference the one
    /// declaring the attribute are walked; the host is derived, never named (see
    /// <c>GenerateIndexGenerator.ReferencesAttributeHost</c> for why a literal broke once).
    /// </summary>
    private static ReferencedIndexMapScan ScanReferenced(Compilation compilation, CancellationToken ct)
    {
        var scan = new ReferencedIndexMapScan();

        var host = compilation.GetTypeByMetadataName(SparkIndexMapAttributeFullName)?.ContainingAssembly?.Name;
        if (host is null) return scan;

        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            ct.ThrowIfCancellationRequested();

            if (reference.Name != host
                && !reference.Modules.Any(m => m.ReferencedAssemblies.Any(identity => identity.Name == host)))
                continue;

            foreach (var type in Types(reference.GlobalNamespace, ct))
            {
                foreach (var member in type.GetMembers())
                {
                    if (member is not (IPropertySymbol or IFieldSymbol)) continue;

                    foreach (var attribute in member.GetAttributes())
                    {
                        if (attribute.AttributeClass?.ToDisplayString() != SparkIndexMapAttributeFullName) continue;
                        scan.Maps.Add(Describe(member, attribute, fromSource: false, ct));
                    }
                }
            }
        }

        return scan;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns, CancellationToken ct)
    {
        foreach (var member in ns.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is INamespaceSymbol nested)
            {
                foreach (var type in Types(nested, ct)) yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var inner in WithNested(type)) yield return inner;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var inner in WithNested(nested))
                yield return inner;
    }
}
