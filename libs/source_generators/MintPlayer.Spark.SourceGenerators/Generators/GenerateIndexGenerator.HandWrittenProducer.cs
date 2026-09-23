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
    private const string RavenIndexes = "global::Raven.Client.Documents.Indexes";

    /// <summary>
    /// Name of the generated method the hand-written index constructor is expected to call. Public API in
    /// practice — renaming it breaks every consumer's constructor.
    /// </summary>
    public const string IndexSearchFieldsMethod = "IndexSearchFields";

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
    {
        foreach (var indexEntity in indexEntities
            .Where(x => !x.IsPartial && x.Companions.Count > 0)
            .OrderBy(x => x.ClassName, System.StringComparer.Ordinal))
        {
            yield return GenerateIndexDiagnostics.ExistingTypeNotPartial.Create(
                indexEntity.Location.ToLocation(compilation), indexEntity.ClassName);
        }

        // The method carrying the Index(...) calls goes on the index class, so that one has to be partial too.
        foreach (var indexEntity in indexEntities
            .Where(x => !x.IsIndexPartial && x.IndexedFields.Count > 0 && x.IndexClassName.Length > 0)
            .OrderBy(x => x.IndexClassName, System.StringComparer.Ordinal))
        {
            yield return GenerateIndexDiagnostics.IndexNotPartial.Create(
                indexEntity.IndexLocation.ToLocation(compilation),
                indexEntity.IndexClassName,
                IndexSearchFieldsMethod);
        }
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var list = indexEntities.Where(x => x is { IsPartial: true, Companions.Count: > 0 }).ToList();
        var indexes = indexEntities
            .Where(x => x is { IsIndexPartial: true, IndexedFields.Count: > 0 })
            .Where(x => x.IndexClassName.Length > 0)
            .ToList();

        if (!knowsSpark || (list.Count == 0 && indexes.Count == 0))
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

        foreach (var group in indexes
            .GroupBy(x => x.IndexPathSpec?.ContainingNamespace ?? string.Empty)
            .OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(group.Key))
            {
                foreach (var indexEntity in group.OrderBy(x => x.IndexClassName, System.StringComparer.Ordinal))
                    WriteIndex(writer, indexEntity, cancellationToken);
                continue;
            }

            writer.WriteLine();
            using (writer.OpenBlock($"namespace {group.Key}"))
            {
                var first = true;
                foreach (var indexEntity in group.OrderBy(x => x.IndexClassName, System.StringComparer.Ordinal))
                {
                    if (!first) writer.WriteLine();
                    first = false;
                    WriteIndex(writer, indexEntity, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Emits the method that applies the declared indexing for a hand-written index.
    /// <para>
    /// The constructor has to call it — a generator cannot add statements to a hand-written constructor body,
    /// only members to the class. That is the same limit that keeps the map assignments hand-written, and it is
    /// why <c>SPARK006</c> exists.
    /// </para>
    /// </summary>
    private static void WriteIndex(
        IndentedTextWriter writer,
        HandWrittenIndexEntityInfo indexEntity,
        CancellationToken cancellationToken)
    {
        using var parents = writer.OpenPathSpec(indexEntity.IndexPathSpec);

        using (writer.OpenBlock($"public partial class {indexEntity.IndexClassName}"))
        {
            writer.WriteLine($"/// <summary>Applies the indexing declared by [Search] on <see cref=\"{indexEntity.FullName}\"/>, and Exact on its DateTimeOffset fields. Call this from the constructor.</summary>");
            using (writer.OpenBlock($"private void {IndexSearchFieldsMethod}()"))
            {
                foreach (var field in indexEntity.IndexedFields)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Guarded because Index(string, FieldIndexing) is a Dictionary.Add, not an
                    // indexer assignment: declaring the same field twice throws ArgumentException
                    // out of CreateIndexDefinition(), i.e. the application fails to start. That is
                    // not a theoretical shape — it is what a half-migrated index looks like while a
                    // constructor call and a base-class call site both exist, and it is what happens
                    // if an author hand-writes an Index(...) for a field the generator also emits.
                    // Skipping a field that is already declared makes the hand-written declaration
                    // win, which is the right precedence: the generator only ever supplies defaults.
                    using (writer.OpenBlock(
                        $"if (!IndexesStrings.ContainsKey(nameof({indexEntity.FullName}.{field.Name})))"))
                    {
                        writer.WriteLine(
                            $"Index(nameof({indexEntity.FullName}.{field.Name}), {RavenIndexes}.FieldIndexing.{field.FieldIndexing});");
                    }

                    // ⚠️ A field that is neither indexed nor stored is REJECTED by Corax, and it
                    // rejects it at map time: the deploy succeeds, then the index sits at
                    // state=Error, entries=0 and every query against it 500s. Measured —
                    // "A field 'DateRaw' that is neither indexed nor stored is useless".
                    //
                    // So FieldIndexing.No obliges us to store the field. This was previously correct
                    // only by accident, because every index carrying a DateTimeOffset also happened to
                    // call StoreAllFields; one that does not simply fails to build. Storing the
                    // WRAPPER is also what makes the offset survive, which is why the base field stays
                    // unstored and is read back from the document.
                    //
                    // Guarded on StoresStrings SEPARATELY from the Index guard above: the two
                    // dictionaries are independent, and an author who hand-wrote only the Store (or
                    // only the Index) would otherwise collide on the other one. Store(string, ...) is
                    // a Dictionary.Add too, so a collision is a startup crash, not a last-write-wins.
                    if (field.FieldIndexing == "No")
                    {
                        using (writer.OpenBlock(
                            $"if (!StoresStrings.ContainsKey(nameof({indexEntity.FullName}.{field.Name})))"))
                        {
                            writer.WriteLine(
                                $"Store(nameof({indexEntity.FullName}.{field.Name}), {RavenIndexes}.FieldStorage.Yes);");
                        }
                    }
                }
            }

            // Only for an index that actually has the base class: `protected override` against a base
            // that declares no such method is a hard CS0115, and an index deriving straight from
            // AbstractIndexCreationTask<T> is still perfectly valid — it just has to keep calling the
            // method from its constructor.
            if (indexEntity.IsSparkIndex)
            {
                writer.WriteLine();
                writer.WriteLine("/// <summary>Called by <c>SparkIndexCreationTask</c> when the index definition is built, so the constructor does not have to remember.</summary>");
                using (writer.OpenBlock("protected override void ConfigureSparkFields()"))
                {
                    writer.WriteLine($"{IndexSearchFieldsMethod}();");
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
