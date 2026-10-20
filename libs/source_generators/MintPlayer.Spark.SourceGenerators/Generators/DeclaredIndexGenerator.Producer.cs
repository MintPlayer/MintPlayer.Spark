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
/// Emits one <c>SparkMultiMapIndexCreationTask&lt;TResult&gt;</c> per declared index name (commerce X19), and reports
/// the declarations it could not use. Both come from <see cref="Plan"/>, so what is emitted and what is reported
/// can never disagree.
/// </summary>
public class DeclaredIndexProducer : Producer, IDiagnosticReporter
{
    private const string RavenIndexes = "global::Raven.Client.Documents.Indexes";

    private readonly List<DeclaredIndexMapInfo> maps;
    private readonly bool emits;

    public DeclaredIndexProducer(List<DeclaredIndexMapInfo> maps, bool emits, string rootNamespace)
        : base(rootNamespace, "SparkDeclaredIndexes.g.cs")
    {
        this.maps = maps;
        this.emits = emits;
    }

    /// <summary>One index to emit: its name, result type and the maps that agree on it, in a stable order.</summary>
    private sealed class IndexPlan
    {
        public string IndexName { get; set; } = string.Empty;
        public string ResultTypeFullName { get; set; } = string.Empty;
        public List<DeclaredIndexMapInfo> Maps { get; } = new();
    }

    /// <summary>
    /// Groups the valid declarations by index name. The first map in ordinal member order fixes the result type;
    /// a map producing another type is reported and left out, so the output stays deterministic whatever order
    /// the references arrive in.
    /// </summary>
    private (List<IndexPlan> Plans, List<(DeclaredIndexMapInfo Map, DeclaredIndexMapInfo Winner)> Mismatches) Plan()
    {
        var plans = new List<IndexPlan>();
        var mismatches = new List<(DeclaredIndexMapInfo, DeclaredIndexMapInfo)>();

        var groups = maps
            .Where(m => m.Error is null)
            .GroupBy(m => m.IndexName, System.StringComparer.Ordinal)
            .OrderBy(g => g.Key, System.StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordered = group
                .GroupBy(m => m.MemberAccess, System.StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(m => m.MemberAccess, System.StringComparer.Ordinal)
                .ToList();

            var winner = ordered[0];
            var plan = new IndexPlan { IndexName = group.Key, ResultTypeFullName = winner.ResultTypeFullName };

            foreach (var map in ordered)
            {
                if (map.ResultTypeFullName == winner.ResultTypeFullName)
                    plan.Maps.Add(map);
                else
                    mismatches.Add((map, winner));
            }

            plans.Add(plan);
        }

        return (plans, mismatches);
    }

    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        if (!emits) yield break;

        foreach (var invalid in maps.Where(m => m.Error is not null).OrderBy(m => m.MemberAccess, System.StringComparer.Ordinal))
        {
            yield return GenerateIndexDiagnostics.InvalidIndexMapDeclaration.Create(
                invalid.Location.ToLocation(compilation), invalid.MemberDisplay, invalid.IndexName, invalid.Error!);
        }

        foreach (var (map, winner) in Plan().Mismatches)
        {
            yield return GenerateIndexDiagnostics.IndexMapResultTypeMismatch.Create(
                map.Location.ToLocation(compilation),
                map.IndexName, map.MemberDisplay, map.ResultTypeFullName, winner.MemberDisplay, winner.ResultTypeFullName);
        }
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        if (!emits) return;

        var plans = Plan().Plans;
        if (plans.Count == 0) return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}.Indexes"))
        {
            for (var i = 0; i < plans.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i > 0) writer.WriteLine();
                WriteIndex(writer, plans[i]);
            }
        }
    }

    private static void WriteIndex(IndentedTextWriter writer, IndexPlan plan)
    {
        var contributors = string.Join(", ", plan.Maps.Select(m => m.MemberDisplay));
        writer.WriteLine($"/// <summary>Declared by [SparkIndexMap] on {Escape(contributors)}. Generated in the application, which owns every index.</summary>");

        using (writer.OpenBlock($"public partial class {plan.IndexName} : global::MintPlayer.Spark.SparkMultiMapIndexCreationTask<{plan.ResultTypeFullName}>"))
        {
            using (writer.OpenBlock($"public {plan.IndexName}()"))
            {
                foreach (var map in plan.Maps)
                    writer.WriteLine($"AddMap<{map.SourceTypeFullName}>(global::MintPlayer.Spark.SparkIndexMaps.Widen({map.MemberAccess}));");

                // Field options come from the result type, so every contribution states the same ones; the union is
                // taken only so a stray map cannot drop one.
                foreach (var field in plan.Maps.SelectMany(m => m.SearchFields).Distinct(System.StringComparer.Ordinal).OrderBy(f => f, System.StringComparer.Ordinal))
                    writer.WriteLine($"Index(nameof({plan.ResultTypeFullName}.{field}), {RavenIndexes}.FieldIndexing.Search);");

                if (plan.Maps.Any(m => m.StoreAllFields))
                    writer.WriteLine($"StoreAllFields({RavenIndexes}.FieldStorage.Yes);");

                // Commerce X18: a [Search] or [Fuzzy] field on the result type needs Lucene; Corax refuses fuzzy queries.
                if (plan.Maps.Any(m => m.UsesLucene))
                    writer.WriteLine($"SearchEngineType = {RavenIndexes}.SearchEngineType.Lucene;");

                writer.WriteLine("OnInitialize();");
            }

            writer.WriteLine();
            writer.WriteLine("/// <summary>Called at the end of the generated constructor. Implement in a hand-written partial to add index configuration.</summary>");
            writer.WriteLine("partial void OnInitialize();");
        }
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
