using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.Spark.SourceGenerators.Naming;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits a RavenDB index and its index-entity (projection) class for every entity marked
/// <c>[GenerateIndex]</c>, replacing the hand-written pair described in
/// <c>docs/guide-queries-and-sorting.md</c>.
/// <para>
/// Both generated types land in the compilation that references this generator — the application project
/// — never in the assembly that declares the entity. Entities routinely live in a lean class library
/// while indexes belong to the app.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public class GenerateIndexGenerator : IncrementalGenerator
{
    private const string GenerateIndexAttributeFullName = "MintPlayer.Spark.Abstractions.GenerateIndexAttribute";

    private const string SparkAbstractionsAssemblyName = "MintPlayer.Spark.Abstractions";

    /// <summary>
    /// Includes nullable reference annotations, so a <c>string?</c> entity property is declared
    /// <c>string?</c> on the index entity rather than silently widening to <c>string</c>.
    /// </summary>
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> valueComparerCacheProvider)
    {
        var entitiesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    if (ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) is not INamedTypeSymbol entity)
                        return default;

                    if (!entity.HasGenerateIndex())
                        return default;

                    return Describe(entity, ct);
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        // Entities declared in a REFERENCED assembly. [GenerateIndex] lives on the entity, which routinely
        // sits in a lean class library, while the index belongs to the app -- so the generator has to see
        // types it has no syntax for.
        //
        // This cannot use CreateSyntaxProvider's incrementality: there is no syntax. The scan therefore
        // re-runs whenever the compilation changes, and what protects downstream work is value comparison on
        // the RESULT. (ICompilationCache is not applicable -- it is constrained to IEqualityComparer values,
        // i.e. it caches comparers, not data.) Cost is bounded by only walking assemblies that reference
        // MintPlayer.Spark.Abstractions at all.
        var referencedEntitiesProvider = context.CompilationProvider
            .Select(static (compilation, ct) => DescribeReferenced(compilation, ct))
            .WithComparer(ComparerRegistry.For<ImmutableArray<GeneratedIndexInfo>>());

        // Emit nothing when the project does not reference MintPlayer.Spark.Abstractions. Paired with the
        // producer's own early return, per house style: the pipeline gate keeps the work out, the producer
        // gate keeps the file out.
        var knowsSparkProvider = context.CompilationProvider
            .Select(static (compilation, ct) =>
                compilation.GetTypeByMetadataName(GenerateIndexAttributeFullName) != null);

        var allEntitiesProvider = entitiesProvider
            .Combine(referencedEntitiesProvider)
            .Select(static (providers, ct) => providers.Left
                .Where(x => x != null)
                .Cast<GeneratedIndexInfo>()
                .Concat(providers.Right)
                .ToImmutableArray())
            .WithComparer(ComparerRegistry.For<ImmutableArray<GeneratedIndexInfo>>());

        var sourceProvider = allEntitiesProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider)
            .Select(static Producer (providers, ct) =>
            {
                var entities = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return new GenerateIndexProducer(
                    entities.ToList(),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        // Hand-written [FromIndex] index entities get their sort companions contributed too. The index entity
        // is always in the application project, so a partial half can always be added to it.
        var handWrittenProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    if (ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) is not INamedTypeSymbol indexEntity)
                        return default;

                    if (!indexEntity.HasFromIndex())
                        return default;

                    return DescribeHandWritten(indexEntity, ct);
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        var handWrittenSourceProvider = handWrittenProvider
            .Combine(allEntitiesProvider)
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider)
            .Select(static Producer (providers, ct) =>
            {
                var handWritten = providers.Left.Left.Left;
                var generated = providers.Left.Left.Right;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                // A generated pair already carries its companions, and the generator does not see its own
                // output. But a hand-written partial half of a *generated* index entity would be matched
                // here, so exclude anything whose name we also generate to avoid duplicate members.
                var generatedNames = new HashSet<string>(
                    generated.Select(x => x.IndexEntityName), System.StringComparer.Ordinal);

                return new HandWrittenSortFieldsProducer(
                    handWritten
                        .Where(x => x != null)
                        .Cast<HandWrittenIndexEntityInfo>()
                        .Where(x => !generatedNames.Contains(x.ClassName))
                        .ToList(),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider, handWrittenSourceProvider);

        // Diagnostics come off the producers themselves via IDiagnosticReporter -- the demonstrated pattern.
        // Producer.Produce can only AddSource and discards exceptions, so a producer cannot report anything
        // from inside ProduceSource; this is the sanctioned second channel.
        context.ReportDiagnostics(
            sourceProvider.Select(static IDiagnosticReporter (producer, ct) => (IDiagnosticReporter)producer),
            handWrittenSourceProvider.Select(static IDiagnosticReporter (producer, ct) => (IDiagnosticReporter)producer));
    }

    /// <summary>
    /// Builds the emission model for one entity, or <c>null</c> when it must not be generated. Every
    /// <c>null</c> here is paired with a diagnostic from <see cref="Collect"/> — see
    /// <see cref="GenerateIndexDiagnostics"/> for why silence is not an option.
    /// </summary>
    private static GeneratedIndexInfo? Describe(INamedTypeSymbol entity, System.Threading.CancellationToken ct)
    {
        var attribute = entity.GetAttributes().First(a =>
            a.AttributeClass?.ToDisplayString() == GenerateIndexAttributeFullName);

        var indexName = GetNamedArgument(attribute, "IndexName") ?? IndexNaming.IndexName(entity.Name);
        var indexEntityName = GetNamedArgument(attribute, "IndexEntityName") ?? IndexNaming.IndexEntityName(entity.Name);

        var itemVariable = IndexNaming.ItemVariable(entity.Name);
        var properties = new List<IndexPropertyInfo>();
        var invalidSearches = new List<InvalidSearchInfo>();
        var ignoredSearches = new List<InvalidSearchInfo>();
        var unrenderableAttributes = new List<InvalidSearchInfo>();

        // [IgnoreProperty] keeps a property out of the index, so a [Search] beside it can never take effect.
        // Reported rather than dropped: the combination reads as "indexed but hidden from the model" and
        // does nothing at all.
        foreach (var property in entity.GetSparkProperties())
        {
            if (!property.IsSearchable() || !property.IsIgnoredForSparkModel()) continue;
            ignoredSearches.Add(new InvalidSearchInfo
            {
                PropertyName = property.Name,
                TypeDisplay = property.Type.ToDisplayString(TypeFormat),
                Location = property.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
            });
        }

        foreach (var property in entity.GetIndexableProperties())
        {
            ct.ThrowIfCancellationRequested();

            var searchable = property.IsSearchable();
            var searchKind = SearchKindOf(property.Type);

            if (searchable && searchKind == SearchKind.Unsupported)
            {
                invalidSearches.Add(new InvalidSearchInfo
                {
                    PropertyName = property.Name,
                    TypeDisplay = property.Type.ToDisplayString(TypeFormat),
                    Location = property.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
                });
            }

            var isSearchableText = searchable && searchKind == SearchKind.Text;

            // A DateTimeOffset is indexed Exact and gets a companion with no attribute at all. DateTime gets
            // neither -- see SparkModelSymbols.IsDateTimeOffset for why that asymmetry is deliberate.
            var isDateTimeOffset = property.Type.IsDateTimeOffset();

            var (fieldAttributes, unrenderable) = AttributeRenderer.ForField(property);
            foreach (var attributeName in unrenderable)
            {
                unrenderableAttributes.Add(new InvalidSearchInfo
                {
                    PropertyName = property.Name,
                    TypeDisplay = attributeName,
                    Location = property.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
                });
            }

            var field = new IndexPropertyInfo
            {
                Name = property.Name,
                TypeDisplay = property.Type.ToDisplayString(TypeFormat),
                NeedsDefaultInitializer = property.Type.IsReferenceType
                    && property.Type.NullableAnnotation != NullableAnnotation.Annotated,
                MapExpression = $"{itemVariable}.{property.Name}",
                FieldIndexing = isSearchableText ? "Search" : isDateTimeOffset ? "Exact" : null,
                Attributes = fieldAttributes,
            };
            properties.Add(field);

            // A searchable text field always gets its companion: analyzing the field is what destroys its
            // sortability, so the two are one decision. The companion is left undeclared on purpose.
            if (isSearchableText || isDateTimeOffset)
                properties.Add(SortCompanionFor(field, AttributeRenderer.ForSortCompanion(property)));
        }

        return new GeneratedIndexInfo
        {
            EntityFullName = entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EntityName = entity.Name,
            IndexName = indexName,
            IndexEntityName = indexEntityName,
            Description = GetNamedArgument(attribute, "Description"),
            CollectionVariable = IndexNaming.CollectionVariable(entity.Name),
            ItemVariable = itemVariable,
            Properties = properties,
            InvalidSearchProperties = invalidSearches,
            IgnoredSearchProperties = ignoredSearches,
            UnrenderableAttributes = unrenderableAttributes,
            Location = entity.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
        };
    }

    /// <summary>
    /// Every <c>[GenerateIndex]</c> entity in a referenced assembly, read from metadata symbols.
    /// <para>Filtered to assemblies that reference <c>MintPlayer.Spark.Abstractions</c>, since an assembly
    /// that does not cannot carry the attribute. Without that filter this walks every type in every
    /// reference, the BCL included.</para>
    /// </summary>
    private static ImmutableArray<GeneratedIndexInfo> DescribeReferenced(Compilation compilation, System.Threading.CancellationToken ct)
    {
        if (compilation.GetTypeByMetadataName(GenerateIndexAttributeFullName) is null)
            return ImmutableArray<GeneratedIndexInfo>.Empty;

        var builder = ImmutableArray.CreateBuilder<GeneratedIndexInfo>();

        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            ct.ThrowIfCancellationRequested();

            if (!ReferencesSparkAbstractions(reference)) continue;

            foreach (var type in AllTypes(reference.GlobalNamespace, ct))
            {
                if (!type.HasGenerateIndex()) continue;
                if (Describe(type, ct) is { } info) builder.Add(info);
            }
        }

        return builder.ToImmutable();
    }

    private static bool ReferencesSparkAbstractions(IAssemblySymbol assembly)
        => assembly.Name == SparkAbstractionsAssemblyName
        || assembly.Modules.Any(module => module.ReferencedAssemblies
            .Any(identity => identity.Name == SparkAbstractionsAssemblyName));

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns, System.Threading.CancellationToken ct)
    {
        foreach (var member in ns.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in AllTypes(nested, ct)) yield return type;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nestedType in NestedTypes(type, ct)) yield return nestedType;
                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type, System.Threading.CancellationToken ct)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            yield return nested;
            foreach (var deeper in NestedTypes(nested, ct)) yield return deeper;
        }
    }

    /// <summary>
    /// Describes what to contribute to a hand-written index entity: a companion per <c>[Search]</c> property
    /// that does not already have one declared by hand.
    /// </summary>
    private static HandWrittenIndexEntityInfo? DescribeHandWritten(INamedTypeSymbol indexEntity, System.Threading.CancellationToken ct)
    {
        var existingNames = new HashSet<string>(
            indexEntity.GetSparkProperties().Select(p => p.Name), System.StringComparer.Ordinal);

        var companions = new List<IndexPropertyInfo>();
        foreach (var property in indexEntity.GetSparkProperties())
        {
            ct.ThrowIfCancellationRequested();

            if (!property.IsSearchable()) continue;
            if (SearchKindOf(property.Type) != SearchKind.Text) continue;

            var companionName = IndexNaming.SortCompanion(property.Name);

            // Already written by hand — contributing it again would be a duplicate member.
            if (existingNames.Contains(companionName)) continue;

            companions.Add(new IndexPropertyInfo
            {
                Name = companionName,
                TypeDisplay = property.Type.ToDisplayString(TypeFormat),
                NeedsDefaultInitializer = property.Type.IsReferenceType
                    && property.Type.NullableAnnotation != NullableAnnotation.Annotated,
                IsSortCompanion = true,
            });
        }

        var isPartial = indexEntity.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<ClassDeclarationSyntax>()
            .Any(c => c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));

        return new HandWrittenIndexEntityInfo
        {
            Namespace = indexEntity.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : indexEntity.ContainingNamespace.ToDisplayString(),
            ClassName = indexEntity.Name,
            PathSpec = indexEntity.GetPathSpec(ct),
            IsPartial = isPartial,
            Companions = companions,
            Location = indexEntity.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
        };
    }

    /// <summary>
    /// The sort companion for a searchable field: same value, same nullability, <c>[IgnoreProperty]</c>, and
    /// deliberately <em>no</em> <c>FieldIndexing</c> — see <see cref="IndexPropertyInfo.FieldIndexing"/>.
    /// <para>The map expression is a byte-identical copy of the base field's. No normalization: lower-casing
    /// or trimming here would make the sort order disagree with the value the user sees, and RavenDB's
    /// default analyzer already lower-cases for comparison purposes.</para>
    /// </summary>
    private static IndexPropertyInfo SortCompanionFor(IndexPropertyInfo field, List<string> attributes) => new()
    {
        Name = IndexNaming.SortCompanion(field.Name),
        TypeDisplay = field.TypeDisplay,
        NeedsDefaultInitializer = field.NeedsDefaultInitializer,
        MapExpression = field.MapExpression,
        FieldIndexing = null,
        IsSortCompanion = true,
        Attributes = attributes,
    };

    private enum SearchKind
    {
        /// <summary>Not a type <c>[Search]</c> can apply to.</summary>
        Unsupported,

        /// <summary><c>string</c>, or a collection of them: analyzed, and gets a companion.</summary>
        Text,

        /// <summary><c>TranslatedString</c>: fans out per language instead. Handled separately.</summary>
        Translated,
    }

    private static SearchKind SearchKindOf(ITypeSymbol type)
    {
        if (type.IsTranslatedString()) return SearchKind.Translated;
        if (type.SpecialType == SpecialType.System_String) return SearchKind.Text;

        // string[] / IEnumerable<string> and friends: RavenDB analyzes each element into the same field.
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String })
            return SearchKind.Text;

        if (type is INamedTypeSymbol { IsGenericType: true } named
            && named.AllInterfaces.Concat([named]).Any(i =>
                i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                && i.TypeArguments.Length == 1
                && i.TypeArguments[0].SpecialType == SpecialType.System_String))
            return SearchKind.Text;

        return SearchKind.Unsupported;
    }

    private static string? GetNamedArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key != name) continue;
            var value = argument.Value.Value as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
