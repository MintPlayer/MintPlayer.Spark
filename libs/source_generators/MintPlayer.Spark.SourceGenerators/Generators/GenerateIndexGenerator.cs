using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.Spark.SourceGenerators.Naming;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;
using System.Collections.Generic;
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

                    return Describe(entity, ctx.SemanticModel.Compilation, ct);
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        // Emit nothing when the project does not reference MintPlayer.Spark.Abstractions. Paired with the
        // producer's own early return, per house style: the pipeline gate keeps the work out, the producer
        // gate keeps the file out.
        var knowsSparkProvider = context.CompilationProvider
            .Select(static (compilation, ct) =>
                compilation.GetTypeByMetadataName(GenerateIndexAttributeFullName) != null);

        var sourceProvider = entitiesProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider)
            .Select(static Producer (providers, ct) =>
            {
                var entities = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return new GenerateIndexProducer(
                    Deduplicate(entities.Where(x => x != null).Cast<GeneratedIndexInfo>()),
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
            .Combine(entitiesProvider)
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
                var generatedNames = new HashSet<string>(generated
                    .Where(x => x != null)
                    .Select(x => x!.IndexEntityName), System.StringComparer.Ordinal);

                return new HandWrittenSortFieldsProducer(
                    handWritten
                        .Where(x => x is { IsPartial: true, Companions.Count: > 0 })
                        .Cast<HandWrittenIndexEntityInfo>()
                        .Where(x => !generatedNames.Contains(x.ClassName)),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider, handWrittenSourceProvider);

        // Diagnostics travel a separate path: Producer.Produce can only AddSource, and it discards
        // exceptions, so nothing inside the producer can report a problem.
        var diagnosticsProvider = entitiesProvider
            .Combine(handWrittenProvider)
            .Combine(knowsSparkProvider)
            .Select(static (providers, ct) => providers.Right
                ? Collect(
                    providers.Left.Left.Where(x => x != null).Cast<GeneratedIndexInfo>().ToList(),
                    providers.Left.Right.Where(x => x != null).Cast<HandWrittenIndexEntityInfo>().ToList())
                : new List<DiagnosticInfo>());

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(diagnosticsProvider),
            static (spc, pair) => spc.ReportDiagnostic(pair.Right.Select(diagnostic =>
                diagnostic.Descriptor.Create(diagnostic.Location.ToLocation(pair.Left), diagnostic.MessageArgs))));
    }

    /// <summary>
    /// Builds the emission model for one entity, or <c>null</c> when it must not be generated. Every
    /// <c>null</c> here is paired with a diagnostic from <see cref="Collect"/> — see
    /// <see cref="GenerateIndexDiagnostics"/> for why silence is not an option.
    /// </summary>
    private static GeneratedIndexInfo? Describe(INamedTypeSymbol entity, Compilation compilation, System.Threading.CancellationToken ct)
    {
        var attribute = entity.GetAttributes().First(a =>
            a.AttributeClass?.ToDisplayString() == GenerateIndexAttributeFullName);

        var indexName = GetNamedArgument(attribute, "IndexName") ?? IndexNaming.IndexName(entity.Name);
        var indexEntityName = GetNamedArgument(attribute, "IndexEntityName") ?? IndexNaming.IndexEntityName(entity.Name);

        var itemVariable = IndexNaming.ItemVariable(entity.Name);
        var properties = new List<IndexPropertyInfo>();
        var invalidSearches = new List<InvalidSearchInfo>();
        var ignoredSearches = new List<InvalidSearchInfo>();

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

            var field = new IndexPropertyInfo
            {
                Name = property.Name,
                TypeDisplay = property.Type.ToDisplayString(TypeFormat),
                NeedsDefaultInitializer = property.Type.IsReferenceType
                    && property.Type.NullableAnnotation != NullableAnnotation.Annotated,
                MapExpression = $"{itemVariable}.{property.Name}",
                FieldIndexing = searchable && searchKind == SearchKind.Text ? "Search" : null,
            };
            properties.Add(field);

            // A searchable text field always gets its companion: analyzing the field is what destroys its
            // sortability, so the two are one decision. The companion is left undeclared on purpose.
            if (searchable && searchKind == SearchKind.Text)
                properties.Add(SortCompanionFor(field));
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
            Location = entity.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
        };
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
    private static IndexPropertyInfo SortCompanionFor(IndexPropertyInfo field) => new()
    {
        Name = IndexNaming.SortCompanion(field.Name),
        TypeDisplay = field.TypeDisplay,
        NeedsDefaultInitializer = field.NeedsDefaultInitializer,
        MapExpression = field.MapExpression,
        FieldIndexing = null,
        IsSortCompanion = true,
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

    /// <summary>
    /// Drops entities that must not be emitted — currently those whose index name collides with another
    /// entity's. The first declaration wins so the emitted output stays deterministic; the loser is
    /// reported by <see cref="Collect"/>.
    /// </summary>
    private static IEnumerable<GeneratedIndexInfo> Deduplicate(IEnumerable<GeneratedIndexInfo> entities)
    {
        var seenIndexNames = new HashSet<string>();
        var result = new List<GeneratedIndexInfo>();
        foreach (var entity in entities.OrderBy(e => e.EntityFullName, System.StringComparer.Ordinal))
        {
            if (entity.Properties.Count == 0) continue;
            if (!seenIndexNames.Add(entity.IndexName)) continue;
            result.Add(entity);
        }

        return result;
    }

    private static List<DiagnosticInfo> Collect(
        List<GeneratedIndexInfo> entities,
        List<HandWrittenIndexEntityInfo> handWritten)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var byIndexName = new Dictionary<string, GeneratedIndexInfo>();

        foreach (var entity in entities.OrderBy(e => e.EntityFullName, System.StringComparer.Ordinal))
        {
            if (entity.Properties.Count == 0)
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Descriptor = GenerateIndexDiagnostics.NoIndexableProperties,
                    Location = entity.Location,
                    MessageArgs = new object[] { entity.EntityFullName },
                });
                continue;
            }

            foreach (var invalid in entity.InvalidSearchProperties)
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Descriptor = GenerateIndexDiagnostics.SearchOnUnsupportedType,
                    Location = invalid.Location,
                    MessageArgs = new object[] { invalid.PropertyName, invalid.TypeDisplay },
                });
            }

            foreach (var ignored in entity.IgnoredSearchProperties)
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Descriptor = GenerateIndexDiagnostics.SearchOnIgnoredProperty,
                    Location = ignored.Location,
                    MessageArgs = new object[] { ignored.PropertyName },
                });
            }

            if (byIndexName.TryGetValue(entity.IndexName, out var winner))
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Descriptor = GenerateIndexDiagnostics.DuplicateIndexName,
                    Location = entity.Location,
                    MessageArgs = new object[] { winner.EntityFullName, entity.EntityFullName, entity.IndexName },
                });
                continue;
            }

            byIndexName.Add(entity.IndexName, entity);
        }

        // Nothing can be contributed to a non-partial index entity, so say so rather than skipping it.
        foreach (var indexEntity in handWritten
            .Where(x => !x.IsPartial && x.Companions.Count > 0)
            .OrderBy(x => x.ClassName, System.StringComparer.Ordinal))
        {
            diagnostics.Add(new DiagnosticInfo
            {
                Descriptor = GenerateIndexDiagnostics.ExistingTypeNotPartial,
                Location = indexEntity.Location,
                MessageArgs = new object[] { indexEntity.ClassName },
            });
        }

        return diagnostics;
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
