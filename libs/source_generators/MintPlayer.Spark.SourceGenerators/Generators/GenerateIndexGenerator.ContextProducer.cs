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
/// Contributes to the application's <c>SparkContext</c> (<c>docs/default_index_PRD.md</c>, D6):
/// <list type="bullet">
/// <item>a typed root for every public <c>[FromIndex]</c> view, generated or hand-written, from this project or a
/// referenced one — <c>VPeople =&gt; Session.Query&lt;VPerson, People_Overview&gt;()</c>. A view root mints no query and
/// has no model effect; it is a typed C# shortcut.</item>
/// <item><c>[SparkPlainRoot(typeof(T), nameof(Context.Root))]</c> for every plain entity root, which the synchronizer
/// reads to resolve a <c>[Reference(typeof(T))]</c> among several roots of <c>T</c>.</item>
/// </list>
/// <para>
/// Every name was resolved in the pipeline (<see cref="ContextRootsPlan"/>, D7); this producer only writes. Root
/// names come from the same <c>IndexNaming</c> functions as the index and companion names rather than a second
/// derivation. The reference design computed names in two independent traversals, which is where "the index and
/// the context disagree" bugs come from.
/// </para>
/// </summary>
public class SparkContextRootsProducer : Producer, IDiagnosticReporter
{
    private const string RavenLinq = "global::Raven.Client.Documents.Linq";
    private const string SparkAbstractions = "global::MintPlayer.Spark.Abstractions";

    private readonly List<ContextRootsPlan> plans;
    private readonly bool knowsSpark;
    private readonly bool knowsPlainRoot;

    /// <param name="knowsPlainRoot">
    /// Whether <c>SparkPlainRootAttribute</c> is in the compilation; without it the marker cannot be written.
    /// </param>
    public SparkContextRootsProducer(
        List<ContextRootsPlan> plans,
        bool knowsSpark,
        bool knowsPlainRoot,
        string rootNamespace)
        : base(rootNamespace, "SparkContextIndexRoots.g.cs")
    {
        this.plans = plans;
        this.knowsSpark = knowsSpark;
        this.knowsPlainRoot = knowsPlainRoot;
    }

    /// <summary>
    /// A non-partial context cannot be extended, so the roots and markers would simply not appear: said out loud
    /// whenever there is anything to add. A root that had to take another name is reported too (D7), so the
    /// member a developer looks for is never silently somewhere else.
    /// </summary>
    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        if (!knowsSpark) yield break;

        foreach (var plan in plans.Where(HasContribution).OrderBy(p => p.Context.FullName, System.StringComparer.Ordinal))
        {
            var location = plan.Context.Location.ToLocation(compilation);

            if (!plan.Context.IsPartial)
            {
                yield return GenerateIndexDiagnostics.ContextNotPartial.Create(location, plan.Context.ClassName);
                continue;
            }

            foreach (var root in plan.ViewRoots.Where(r => r.MemberName != r.WantedMemberName))
            {
                yield return GenerateIndexDiagnostics.ViewRootRenamed.Create(
                    location, plan.Context.ClassName, root.ViewFullName, root.WantedMemberName, root.MemberName);
            }
        }
    }

    private bool HasContribution(ContextRootsPlan plan)
        => plan.ViewRoots.Count > 0 || (knowsPlainRoot && plan.Context.PlainRoots.Count > 0);

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        // A context with nothing to add contributes nothing: an empty partial would be harmless but pointless noise.
        var targets = plans
            .Where(p => p.Context.IsPartial && HasContribution(p))
            .ToList();

        if (!knowsSpark || targets.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        foreach (var group in targets
            .GroupBy(p => p.Context.PathSpec?.ContainingNamespace ?? string.Empty)
            .OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(group.Key))
            {
                foreach (var plan in group.OrderBy(p => p.Context.ClassName, System.StringComparer.Ordinal))
                    WriteContext(writer, plan, cancellationToken);
                continue;
            }

            using (writer.OpenBlock($"namespace {group.Key}"))
            {
                var first = true;
                foreach (var plan in group.OrderBy(p => p.Context.ClassName, System.StringComparer.Ordinal))
                {
                    if (!first) writer.WriteLine();
                    first = false;
                    WriteContext(writer, plan, cancellationToken);
                }
            }
        }
    }

    private void WriteContext(IndentedTextWriter writer, ContextRootsPlan plan, CancellationToken cancellationToken)
    {
        var context = plan.Context;
        using var parents = writer.OpenPathSpec(context.PathSpec);

        if (knowsPlainRoot)
        {
            foreach (var root in context.PlainRoots)
            {
                writer.WriteLine(
                    $"[{SparkAbstractions}.SparkPlainRootAttribute(typeof({root.EntityFullName}), nameof({context.FullName}.{root.PropertyName}))]");
            }
        }

        using (writer.OpenBlock($"public partial class {context.ClassName}"))
        {
            var first = true;

            foreach (var root in plan.ViewRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!first) writer.WriteLine();
                first = false;

                writer.WriteLine($"/// <summary>Index-backed query root for <see cref=\"{root.ViewFullName}\"/>.</summary>");
                writer.WriteLine(
                    $"public {RavenLinq}.IRavenQueryable<{root.ViewFullName}> {root.MemberName}"
                    + $" => Session.Query<{root.ViewFullName}, {root.IndexFullName}>();");
            }
        }
    }
}
