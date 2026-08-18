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
            .Select(static (providers, ct) =>
            {
                var entities = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new GenerateIndexProducer(
                    Deduplicate(entities.Where(x => x != null).Cast<GeneratedIndexInfo>()),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);

        // Diagnostics travel a separate path: Producer.Produce can only AddSource, and it discards
        // exceptions, so nothing inside the producer can report a problem.
        var diagnosticsProvider = entitiesProvider
            .Combine(knowsSparkProvider)
            .Select(static (providers, ct) => providers.Right
                ? Collect(providers.Left.Where(x => x != null).Cast<GeneratedIndexInfo>().ToList())
                : new List<DiagnosticInfo>());

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(diagnosticsProvider),
            static (spc, pair) =>
            {
                var (compilation, diagnostics) = pair;
                foreach (var diagnostic in diagnostics)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        diagnostic.Descriptor,
                        diagnostic.Location.ToLocation(compilation) ?? Location.None,
                        diagnostic.MessageArgs));
                }
            });
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

        var properties = new List<IndexPropertyInfo>();
        foreach (var property in entity.GetIndexableProperties())
        {
            ct.ThrowIfCancellationRequested();
            properties.Add(new IndexPropertyInfo
            {
                Name = property.Name,
                TypeDisplay = property.Type.ToDisplayString(TypeFormat),
                NeedsDefaultInitializer = property.Type.IsReferenceType
                    && property.Type.NullableAnnotation != NullableAnnotation.Annotated,
            });
        }

        return new GeneratedIndexInfo
        {
            EntityFullName = entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EntityName = entity.Name,
            IndexName = indexName,
            IndexEntityName = indexEntityName,
            Description = GetNamedArgument(attribute, "Description"),
            CollectionVariable = IndexNaming.CollectionVariable(entity.Name),
            ItemVariable = IndexNaming.ItemVariable(entity.Name),
            Properties = properties,
            Location = entity.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
        };
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

    private static List<DiagnosticInfo> Collect(List<GeneratedIndexInfo> entities)
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
