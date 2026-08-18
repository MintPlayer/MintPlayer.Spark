using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Contributes sort companions to hand-written <c>[FromIndex]</c> index entities.
/// <para>
/// Separate from the generated pair on purpose: here the developer owns the class, the index and the map,
/// and the generator supplies only the declarations. It cannot supply the map assignments, because a
/// generator adds members to a partial class and cannot reach inside a hand-written object initializer —
/// so the companions it emits here are inert until the developer maps them. The missing-assignment case is
/// covered by an analyzer rather than left to documentation.
/// </para>
/// </summary>
public class HandWrittenSortFieldsProducer : Producer, IDiagnosticReporter
{
    private const string SparkAbstractions = "global::MintPlayer.Spark.Abstractions";

    private readonly IEnumerable<HandWrittenIndexEntityInfo> indexEntities;
    private readonly bool knowsSpark;

    public HandWrittenSortFieldsProducer(
        IEnumerable<HandWrittenIndexEntityInfo> indexEntities,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "SparkIndexEntitySortFields.g.cs")
    {
        this.indexEntities = indexEntities;
        this.knowsSpark = knowsSpark;
    }

    /// <summary>
    /// Nothing can be contributed to a non-partial index entity, so say so rather than skipping it.
    /// </summary>
    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
        => indexEntities
            .Where(x => !x.IsPartial && x.Companions.Count > 0)
            .OrderBy(x => x.ClassName, System.StringComparer.Ordinal)
            .Select(x => GenerateIndexDiagnostics.ExistingTypeNotPartial.Create(
                x.Location.ToLocation(compilation), x.ClassName));

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var list = indexEntities.Where(x => x is { IsPartial: true, Companions.Count: > 0 }).ToList();

        if (!knowsSpark || list.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        // Grouped so two index entities in one namespace share a namespace block.
        foreach (var group in list
            .GroupBy(x => x.PathSpec?.ContainingNamespace ?? x.Namespace)
            .OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(group.Key))
            {
                foreach (var indexEntity in group.OrderBy(x => x.ClassName, System.StringComparer.Ordinal))
                    WriteIndexEntity(writer, indexEntity, cancellationToken);
                continue;
            }

            using (writer.OpenBlock($"namespace {group.Key}"))
            {
                var first = true;
                foreach (var indexEntity in group.OrderBy(x => x.ClassName, System.StringComparer.Ordinal))
                {
                    if (!first) writer.WriteLine();
                    first = false;
                    WriteIndexEntity(writer, indexEntity, cancellationToken);
                }
            }
        }
    }

    private static void WriteIndexEntity(
        IndentedTextWriter writer,
        HandWrittenIndexEntityInfo indexEntity,
        CancellationToken cancellationToken)
    {
        // Reopens every containing type before the index entity itself. A nested index entity emitted as a
        // top-level class in its namespace would not compile.
        using var parents = writer.OpenPathSpec(indexEntity.PathSpec);

        using (writer.OpenBlock($"public partial class {indexEntity.ClassName}"))
        {
            foreach (var companion in indexEntity.Companions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                writer.WriteLine($"[{SparkAbstractions}.IgnorePropertyAttribute]");
                var initializer = companion.NeedsDefaultInitializer ? " = default!;" : string.Empty;
                writer.WriteLine($"public {companion.TypeDisplay} {companion.Name} {{ get; set; }}{initializer}");
            }
        }
    }
}
