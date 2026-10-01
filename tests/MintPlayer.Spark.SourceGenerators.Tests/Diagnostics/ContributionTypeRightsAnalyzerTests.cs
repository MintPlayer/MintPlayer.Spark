using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using MintPlayer.Spark.SourceGenerators.Tests.Generators;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// Contributions M5b: SPARK014 (the attribute-level right validator) accepts rights on the types the
/// Contributions generator emits (<c>SongLyricsContribution</c>, <c>SongLyricsCurrent</c>) in the build
/// that first generates them — before synchronize has written their model files. It judges them by
/// the generated class (the CLR fallback), so it still refuses an attribute the class does not have.
/// </summary>
/// <remarks>
/// The real generator runs first and the analyzer sees its output, as in <c>csc</c>. A type-level
/// right on a generated type is SPARK012's business, which warns (never errors) until synchronize —
/// documented in the library README.
/// </remarks>
public class ContributionTypeRightsAnalyzerTests
{
    private const string SongModelJson = """
        {
          "persistentObject": {
            "id": "33333333-3333-3333-3333-333333333333",
            "name": "Song",
            "clrType": "TestApp.Song",
            "attributes": [ { "name": "Title" }, { "name": "Lyrics" } ]
          }
        }
        """;

    private static string SecurityJson(params string[] resources)
    {
        var entries = string.Join(",\n", resources.Select((r, i) =>
            $$"""{ "id": "22222222-2222-2222-2222-00000000000{{i}}", "resource": "{{r}}", "groupId": "00000000-0000-0000-0000-000000000001" }"""));
        return $$"""
            {
              "wellKnown": { "authenticated": "00000000-0000-0000-0000-000000000001" },
              "groups": { "00000000-0000-0000-0000-000000000001": { "en": "Moderators" } },
              "rights": [
                {{entries}}
              ]
            }
            """;
    }

    private static async Task<IReadOnlyList<Diagnostic>> RunAsync(params string[] resources)
    {
        var analyzer = GeneratorHarness.CreateAnalyzer("SecurityConfigurationAnalyzer");
        var run = await GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            ContributionsGeneratorTests.GeneratorName,
            [analyzer],
            [("Song.cs", ContributionsGeneratorTests.SongSource("ContributionAttribution.Contributor | ContributionAttribution.UpdatedAt | ContributionAttribution.History"))],
            referenceTypes: [.. ContributionsGeneratorTests.References(softDelete: true), .. SecurityConfigurationAnalyzerTests.ReservedVerbSources],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("C:\\app\\App_Data\\security.json", SecurityJson(resources)),
                ("C:\\app\\App_Data\\Model\\Song.json", SongModelJson),
            ],
            generatorAssemblyName: ContributionsGeneratorTests.GeneratorAssembly);

        run.GeneratedTreePaths.Should().NotBeEmpty("the generator must have emitted the contribution types");
        return [.. run.Diagnostics];
    }

    [Theory]
    [InlineData("Query/SongLyricsContribution/Text")]
    [InlineData("QueryRead/SongLyricsContribution/ContributorId")]
    [InlineData("Read/SongLyricsContribution/Language")]
    [InlineData("Query/SongLyricsCurrent/Text")]
    [InlineData("read/songlyricscurrent/contributioncount")]
    public async Task An_attribute_right_on_a_generated_type_is_accepted_before_synchronize(string resource)
    {
        var diagnostics = await RunAsync(resource);

        diagnostics.Where(d => d.Id == "SPARK014").Should().BeEmpty();
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task An_attribute_the_generated_type_does_not_have_is_still_an_error()
    {
        var diagnostics = await RunAsync("Query/SongLyricsContribution/Lyricz");

        var diagnostic = diagnostics.Where(d => d.Id == "SPARK014").Should().ContainSingle().Which;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("does not declare", "the type resolved through its generated class");
    }

    [Theory]
    [InlineData("RevertContribution/SongLyricsContribution")]
    [InlineData("Delete/SongLyricsCurrent")]
    public async Task A_type_level_right_on_a_generated_type_is_never_an_error(string resource)
    {
        var diagnostics = await RunAsync(resource);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        diagnostics.Where(d => d.Id == "SPARK011").Should().BeEmpty("RevertContribution is a reserved verb of Contributions, Delete one of core");
        diagnostics.Where(d => d.Id is not ("SPARK012")).Should().BeEmpty("only the documented until-synchronize SPARK012 warning may remain");
    }
}
