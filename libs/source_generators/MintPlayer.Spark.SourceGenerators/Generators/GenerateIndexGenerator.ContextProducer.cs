using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.Spark.SourceGenerators.Naming;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Contributes an index-backed query root to the application's <c>SparkContext</c> for each generated pair —
/// the member Fleet and HR write by hand today, and DemoApp omits entirely.
/// <para>
/// Root names come from the same <see cref="IndexNaming"/> functions as the index and companion names rather
/// than a second derivation. The reference design computed names in two independent traversals, which is where
/// "the index and the context disagree" bugs come from.
/// </para>
/// </summary>
public class SparkContextRootsProducer : Producer, IDiagnosticReporter
{
    private const string RavenLinq = "global::Raven.Client.Documents.Linq";

    private readonly IEnumerable<SparkContextInfo> contexts;
    private readonly IEnumerable<GeneratedIndexInfo> entities;
    private readonly bool knowsSpark;

    public SparkContextRootsProducer(
        IEnumerable<SparkContextInfo> contexts,
        IEnumerable<GeneratedIndexInfo> entities,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "SparkContextIndexRoots.g.cs")
    {
        this.contexts = contexts;
        this.entities = entities;
        this.knowsSpark = knowsSpark;
    }

    /// <summary>
    /// A non-partial context cannot be extended, so the roots would simply not appear. Said out loud instead.
    /// </summary>
    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        if (!knowsSpark || !Roots().Any()) yield break;

        var roots = Roots();
        foreach (var context in contexts
            .Where(c => !c.IsPartial && RootsFor(c, roots).Count > 0)
            .OrderBy(c => c.ClassName, System.StringComparer.Ordinal))
        {
            yield return GenerateIndexDiagnostics.ContextNotPartial.Create(
                context.Location.ToLocation(compilation), context.ClassName);
        }
    }

    /// <summary>Index-entity name paired with the root member name it should be exposed as.</summary>
    private List<(string IndexEntityName, string IndexName, string MemberName)> Roots()
        => entities
            .Where(e => e.Properties.Count > 0)
            .GroupBy(e => e.IndexName, System.StringComparer.Ordinal)
            .Select(g => g.OrderBy(e => e.EntityFullName, System.StringComparer.Ordinal).First())
            .OrderBy(e => e.IndexEntityName, System.StringComparer.Ordinal)
            .Select(e => (e.IndexEntityName, e.IndexName, IndexNaming.ContextRoot(e.EntityName, e.IndexEntityName)))
            .ToList();

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var roots = Roots();

        // A context whose every root is already declared by hand contributes nothing. Emitting an empty
        // partial for it would be harmless but pointless noise in the generated output.
        var targets = contexts
            .Where(c => c.IsPartial && RootsFor(c, roots).Count > 0)
            .ToList();

        if (!knowsSpark || roots.Count == 0 || targets.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        foreach (var group in targets
            .GroupBy(c => c.PathSpec?.ContainingNamespace ?? string.Empty)
            .OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(group.Key))
            {
                foreach (var context in group.OrderBy(c => c.ClassName, System.StringComparer.Ordinal))
                    WriteContext(writer, context, roots, cancellationToken);
                continue;
            }

            using (writer.OpenBlock($"namespace {group.Key}"))
            {
                var first = true;
                foreach (var context in group.OrderBy(c => c.ClassName, System.StringComparer.Ordinal))
                {
                    if (!first) writer.WriteLine();
                    first = false;
                    WriteContext(writer, context, roots, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Roots this context does not already declare. A hand-written root wins: emitting ours too would be a
    /// duplicate-member error, and overriding is legitimate rather than a mistake worth reporting.
    /// </summary>
    private static List<(string IndexEntityName, string IndexName, string MemberName)> RootsFor(
        SparkContextInfo context,
        List<(string IndexEntityName, string IndexName, string MemberName)> roots)
    {
        var existing = new HashSet<string>(context.ExistingMemberNames, System.StringComparer.Ordinal);
        return roots.Where(r => !existing.Contains(r.MemberName)).ToList();
    }

    private void WriteContext(
        IndentedTextWriter writer,
        SparkContextInfo context,
        List<(string IndexEntityName, string IndexName, string MemberName)> roots,
        CancellationToken cancellationToken)
    {
        using var parents = writer.OpenPathSpec(context.PathSpec);

        using (writer.OpenBlock($"public partial class {context.ClassName}"))
        {
            var first = true;

            foreach (var (indexEntityName, indexName, memberName) in RootsFor(context, roots))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!first) writer.WriteLine();
                first = false;

                writer.WriteLine($"/// <summary>Index-backed query root for <see cref=\"{IndexNamespace}.{indexEntityName}\"/>.</summary>");
                writer.WriteLine(
                    $"public {RavenLinq}.IRavenQueryable<{IndexNamespace}.{indexEntityName}> {memberName}"
                    + $" => Session.Query<{IndexNamespace}.{indexEntityName}, {IndexNamespace}.{indexName}>();");
            }
        }
    }

    private string IndexNamespace => $"global::{RootNamespace}.Indexes";
}
