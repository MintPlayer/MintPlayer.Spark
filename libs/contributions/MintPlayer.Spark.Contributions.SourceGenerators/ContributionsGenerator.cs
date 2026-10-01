using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.Contributions.SourceGenerators;

/// <summary>
/// Generates the contribution and current types, their metadata and their registration for every
/// <c>[Contribution]</c> property (PRD T3).
/// </summary>
/// <remarks>
/// <para>
/// M3 skeleton: the pipeline is in place and finds the properties, but emits nothing yet; M4 fills
/// it in. The attribute is matched by metadata name, so the generator needs no reference to the
/// Abstractions assembly, and <c>ForAttributeWithMetadataName</c> sees records as well as classes.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ContributionsGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Metadata name of <c>ContributionAttribute</c>. Shared with SPARK017 in
    /// <c>MintPlayer.Spark.SourceGenerators</c>; the two must move together.
    /// </summary>
    internal const string ContributionAttributeMetadataName = "MintPlayer.Spark.Contributions.ContributionAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Project to a string straight away: symbols must not flow through the incremental pipeline,
        // or every keystroke re-runs the output step.
        var properties = context.SyntaxProvider.ForAttributeWithMetadataName(
            ContributionAttributeMetadataName,
            predicate: static (_, _) => true,
            transform: static (ctx, _) => ctx.TargetSymbol is IPropertySymbol property
                ? $"{property.ContainingType.ToDisplayString()}.{property.Name}"
                : null);

        context.RegisterSourceOutput(properties.Collect(), static (_, _) =>
        {
            // M4: emit {Target}{Property}Contribution, {Target}{Property}Current, the metadata class
            // and the registration.
        });
    }
}
