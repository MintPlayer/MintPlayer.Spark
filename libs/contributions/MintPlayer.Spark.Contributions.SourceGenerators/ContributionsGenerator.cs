using Microsoft.CodeAnalysis;
using System.Text;

namespace MintPlayer.Spark.Contributions.SourceGenerators;

/// <summary>
/// Generates the contribution and current types, their metadata and their registration for every
/// <c>[Contribution]</c> property (PRD T3, Q6, Q9, §4.1 S-C3).
/// </summary>
/// <remarks>
/// <para>
/// Per declaration <c>Owner.Property</c> with element <c>E</c>, in the owner's namespace:
/// <c>{Owner}{Property}Contribution</c>, <c>{Owner}{Property}Current</c> and
/// <c>{Owner}{Property}ContributionMetadata</c> (a <c>ContributionDescriptor</c>). Per element, in its own
/// namespace: a partial with the get-only <c>[ValueKey] Key</c> (collections) and the attribution
/// properties the declarations ask for. Per assembly: one <c>[ModuleInitializer]</c> that registers each
/// element's key with <c>SparkValueObjects</c> and each descriptor with <c>ContributionRegistry</c>.
/// </para>
/// <para>
/// The generator reports nothing: <see cref="ContributionsAnalyzer"/> reports the rules, from the same
/// <see cref="ContributionInspector"/> reading, and a declaration with a blocking issue simply produces
/// no output. The attribute is matched by metadata name, and <c>ForAttributeWithMetadataName</c> sees
/// records as well as classes.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ContributionsGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Metadata name of <c>ContributionAttribute</c>. Shared with SPARK017 in
    /// <c>MintPlayer.Spark.SourceGenerators</c>; the two must move together.
    /// </summary>
    internal const string ContributionAttributeMetadataName = ContributionInspector.ContributionAttributeMetadataName;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The transform projects to a value-equal model straight away: symbols must not flow through
        // the incremental pipeline, or every keystroke re-runs the output step.
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                ContributionAttributeMetadataName,
                predicate: static (_, _) => true,
                transform: static (ctx, ct) => ctx.TargetSymbol is IPropertySymbol property && ctx.Attributes.Length > 0
                    ? ContributionInspector.Inspect(property, ctx.Attributes[0], ctx.SemanticModel.Compilation, ct).Model
                    : null)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var rootNamespace = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName))
            .Select(static (pair, _) =>
                pair.Left.GlobalOptions.TryGetValue("build_property.rootnamespace", out var ns) && !string.IsNullOrWhiteSpace(ns)
                    ? ns
                    : SanitizeNamespace(pair.Right));

        context.RegisterSourceOutput(models.Collect().Combine(rootNamespace), static (spc, input) =>
        {
            var (all, root) = input;
            if (all.IsDefaultOrEmpty)
                return;

            // Two declarations of one name (a partial owner split across files) would emit twice.
            var declarations = all
                .GroupBy(m => m.ContributionType, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(m => m.ContributionType, StringComparer.Ordinal)
                .ToList();

            foreach (var model in declarations)
                spc.AddSource(HintName(model.Namespace + "." + model.BaseName) + ContributionInspector.GeneratedFileSuffix,
                    ContributionsEmitter.Declaration(model));

            foreach (var element in declarations.GroupBy(m => m.Element.FullyQualified, StringComparer.Ordinal))
            {
                var first = element.First();
                var slots = element.Where(m => m.IsCollection).Select(m => m.Slots).FirstOrDefault();
                var attribution = element.Aggregate(0, (acc, m) => acc | m.Attribution);
                if (slots.Length == 0 && attribution == 0)
                    continue;

                spc.AddSource(HintName(first.Element.FullyQualified) + ".Element" + ContributionInspector.GeneratedFileSuffix,
                    ContributionsEmitter.Element(first.Element, slots.ToList(), attribution));
            }

            spc.AddSource("SparkContributionsRegistration" + ContributionInspector.GeneratedFileSuffix,
                ContributionsEmitter.Registration(root, declarations));
        });
    }

    private static string HintName(string fullyQualified)
    {
        var sb = new StringBuilder();
        foreach (var ch in fullyQualified.Replace("global::", ""))
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' ? ch : '_');
        return sb.ToString().TrimStart('.');
    }

    private static string SanitizeNamespace(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return "SparkContributions";

        var segments = assemblyName!.Split('.')
            .Select(s => new string(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray()))
            .Where(s => s.Length > 0)
            .Select(s => char.IsDigit(s[0]) ? "_" + s : s);
        var joined = string.Join(".", segments);
        return joined.Length == 0 ? "SparkContributions" : joined;
    }
}
