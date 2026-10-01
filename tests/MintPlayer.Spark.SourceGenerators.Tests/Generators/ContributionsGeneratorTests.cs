using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Tests for the contributions generator, which ships inside the MintPlayer.Spark.Contributions
/// package rather than with the other Spark generators.
/// </summary>
public class ContributionsGeneratorTests
{
    private const string GeneratorName = "ContributionsGenerator";
    private const string GeneratorAssembly = "MintPlayer.Spark.Contributions.SourceGenerators";

    /// <summary>
    /// M3 skeleton: the generator loads from its own assembly, runs its <c>[Contribution]</c> pipeline
    /// over a real declaration, and emits nothing and reports nothing. M4 replaces this with snapshots.
    /// </summary>
    [Fact]
    public void Skeleton_runs_over_a_contribution_property_without_output_or_diagnostics()
    {
        const string source = """
            using System.Collections.Generic;
            using MintPlayer.Spark.Contributions;

            namespace TestApp;

            public partial class Song
            {
                public string? Id { get; set; }

                [Contribution(Attribution = ContributionAttribution.Contributor | ContributionAttribution.History)]
                public List<Lyrics> Lyrics { get; set; } = new();
            }

            public partial class Lyrics
            {
                [ContributionSlot] public string Language { get; set; } = "";
                [ContributionSlot] public string Script { get; set; } = "";
                public string Text { get; set; } = "";
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ContributionAttribute)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.FinalCompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.GeneratedSources.Should().BeEmpty();
    }
}
