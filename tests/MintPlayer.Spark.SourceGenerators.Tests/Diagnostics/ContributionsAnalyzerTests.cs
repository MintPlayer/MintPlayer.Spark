using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using MintPlayer.Spark.SourceGenerators.Tests.Generators;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// The <c>[Contribution]</c> rules (contributions M4, PRD T2/T4/Q4): SPARK025–SPARK029 and
/// SPARK031–SPARK035, and the two code fixes (SPARK029, SPARK031).
/// </summary>
/// <remarks>
/// The analyzer runs on the post-generator compilation, the shape <c>csc</c> hands it, so a test also
/// proves the analyzer does not flag the members the generator added (the element's <c>Key</c> and
/// attribution properties carry reserved names).
/// </remarks>
public class ContributionsAnalyzerTests
{
    private const string Assembly = ContributionsGeneratorTests.GeneratorAssembly;

    private static string Fixture(string property, string element, string owner = "public string? Id { get; set; }", string usings = "") => $$"""
        using System;
        using System.Collections.Generic;
        using MintPlayer.Spark.Contributions;
        {{usings}}

        namespace TestApp;

        public class Song
        {
            {{owner}}

            {{property}}
        }

        {{element}}
        """;

    private const string LyricsProperty = "[Contribution] [Newtonsoft.Json.JsonIgnore] public List<Lyrics> Lyrics { get; set; } = new();";

    private static string Lyrics(string slots = """
            [ContributionSlot] public string Language { get; set; } = "";
                [ContributionSlot] public string Script { get; set; } = "";
            """, string values = "public string Text { get; set; } = \"\";", string header = "public partial class Lyrics") => $$"""
        {{header}}
        {
            {{slots}}
            {{values}}
        }
        """;

    private static async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(
        string source, bool softDelete = true, IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var analyzer = (DiagnosticAnalyzer)GeneratorHarness.InstantiateComponent("ContributionsAnalyzer", Assembly, typeof(DiagnosticAnalyzer));
        var result = await GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            ContributionsGeneratorTests.GeneratorName, [analyzer], [("Fixture.cs", source)],
            referenceTypes: ContributionsGeneratorTests.References(softDelete), rootNamespace: "TestApp",
            additionalReferences: additionalReferences, generatorAssemblyName: Assembly);

        var errors = result.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        errors.Should().BeEmpty(string.Join(Environment.NewLine, errors));
        return result.Diagnostics.ToList();
    }

    private static IEnumerable<string> Ids(IEnumerable<Diagnostic> diagnostics) => diagnostics.Select(d => d.Id).OrderBy(x => x);

    [Fact]
    public async Task A_valid_declaration_reports_nothing_including_on_the_generated_members()
    {
        var diagnostics = await AnalyzeAsync(ContributionsGeneratorTests.SongSource(
            "ContributionAttribution.Contributor | ContributionAttribution.UpdatedAt | ContributionAttribution.History"));
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_element_is_fine()
    {
        var diagnostics = await AnalyzeAsync(ContributionsGeneratorTests.SongSource("ContributionAttribution.None", elementKind: "record"));
        diagnostics.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- SPARK025 slot types (T2)

    [Theory]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("decimal")]
    [InlineData("DateTime")]
    [InlineData("DateTimeOffset")]
    [InlineData("DateTime?")]
    [InlineData("char")]
    [InlineData("TimeSpan")]
    public async Task SPARK025_refuses_a_slot_type_without_a_stable_text_form(string type)
    {
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty, Lyrics(slots: $"[ContributionSlot] public {type} Version {{ get; set; }}")));
        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK025");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("Version");
    }

    [Theory]
    [InlineData("string")]
    [InlineData("string?")]
    [InlineData("Region")]
    [InlineData("Region?")]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint?")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("Guid")]
    [InlineData("Guid?")]
    [InlineData("bool")]
    [InlineData("bool?")]
    public async Task SPARK025_accepts_every_T2_slot_type(string type)
    {
        var element = Lyrics(slots: $"[ContributionSlot] public {type} Version {{ get; set; }}") + "\npublic enum Region { World, Europe }";
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty, element));
        diagnostics.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- SPARK026 cardinality

    [Fact]
    public async Task SPARK026_refuses_a_single_valued_property_whose_element_has_slots()
    {
        var diagnostics = await AnalyzeAsync(Fixture(
            "[Contribution] [Newtonsoft.Json.JsonIgnore] public Lyrics? Lyrics { get; set; }", Lyrics()));
        Ids(diagnostics).Should().Equal("SPARK026");
    }

    [Fact]
    public async Task SPARK026_refuses_a_collection_whose_element_has_no_slots()
    {
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty, Lyrics(slots: "")));
        Ids(diagnostics).Should().Equal("SPARK026");
    }

    // ---------------------------------------------------------------- SPARK027 no values

    [Fact]
    public async Task SPARK027_refuses_an_element_with_only_slots()
    {
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty, Lyrics(values: "public string Computed => Language;")));
        Ids(diagnostics).Should().Equal("SPARK027");
    }

    // ---------------------------------------------------------------- SPARK028 owner

    [Fact]
    public async Task SPARK028_warns_when_the_owner_has_no_string_id_and_still_generates()
    {
        var analyzer = (DiagnosticAnalyzer)GeneratorHarness.InstantiateComponent("ContributionsAnalyzer", Assembly, typeof(DiagnosticAnalyzer));
        var result = await GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            ContributionsGeneratorTests.GeneratorName, [analyzer],
            [("Fixture.cs", Fixture(LyricsProperty, Lyrics(), owner: "public int Number { get; set; }"))],
            referenceTypes: ContributionsGeneratorTests.References(softDelete: true), rootNamespace: "TestApp",
            generatorAssemblyName: Assembly);

        var diagnostic = result.Diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK028");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        result.GeneratedTreePaths.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------- SPARK029 JsonIgnore

    [Fact]
    public async Task SPARK029_refuses_a_property_without_newtonsofts_JsonIgnore()
    {
        var diagnostics = await AnalyzeAsync(Fixture("[Contribution] public List<Lyrics> Lyrics { get; set; } = new();", Lyrics()));
        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK029");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SPARK029_is_not_satisfied_by_System_Text_Json_JsonIgnore()
    {
        var diagnostics = await AnalyzeAsync(Fixture(
            "[Contribution] [System.Text.Json.Serialization.JsonIgnore] public List<Lyrics> Lyrics { get; set; } = new();", Lyrics()));
        Ids(diagnostics).Should().Equal("SPARK029");
    }

    [Fact]
    public async Task SPARK029_fix_adds_the_fully_qualified_attribute()
    {
        const string song = """
            using System.Collections.Generic;
            using MintPlayer.Spark.Contributions;

            namespace TestApp;

            public class Song
            {
                public string? Id { get; set; }

                [Contribution]
                public List<Lyrics> Lyrics { get; set; } = new();
            }

            public partial class Lyrics
            {
                [ContributionSlot] public string Language { get; set; } = "";
                public string Text { get; set; } = "";
            }
            """;

        var result = await CodeFixHarness.RunAnalyzerFixAsync(
            "ContributionsAnalyzer", "ContributionJsonIgnoreCodeFixProvider", "SPARK029",
            [FixtureProject.Of("App", ("Song.cs", song))],
            referenceTypes: ContributionsGeneratorTests.References(softDelete: true),
            analyzerAssemblyName: Assembly, codeFixAssemblyName: Assembly);

        result.OfferedTitles.Should().Equal("Add [Newtonsoft.Json.JsonIgnore]");
        result.Document("Song.cs").Should().Contain(
            "    [Contribution]\n    [Newtonsoft.Json.JsonIgnore]\n    public List<Lyrics> Lyrics { get; set; } = new();");
    }

    // ---------------------------------------------------------------- SPARK031 partial

    [Fact]
    public async Task SPARK031_refuses_an_element_that_is_not_partial()
    {
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty, Lyrics(header: "public class Lyrics")));
        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK031");
        diagnostic.Properties["SparkContributionElement"].Should().Be("TestApp.Lyrics");
    }

    [Fact]
    public async Task SPARK031_does_not_apply_when_nothing_is_added_to_the_element()
    {
        // Single-valued, no slots, no attribution: the element gets no partial.
        var diagnostics = await AnalyzeAsync(Fixture(
            "[Contribution] [Newtonsoft.Json.JsonIgnore] public Biography? Biography { get; set; }",
            "public class Biography { public string Text { get; set; } = \"\"; }"));
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task SPARK031_fix_makes_the_element_and_its_containing_type_partial()
    {
        const string song = """
            using System.Collections.Generic;
            using MintPlayer.Spark.Contributions;

            namespace TestApp;

            public class Song
            {
                public string? Id { get; set; }

                [Contribution, Newtonsoft.Json.JsonIgnore]
                public List<Catalog.Lyrics> Lyrics { get; set; } = new();
            }
            """;
        const string catalog = """
            using MintPlayer.Spark.Contributions;

            namespace TestApp;

            public static class Catalog
            {
                public class Lyrics
                {
                    [ContributionSlot] public string Language { get; set; } = "";
                    public string Text { get; set; } = "";
                }
            }
            """;

        var result = await CodeFixHarness.RunAnalyzerFixAsync(
            "ContributionsAnalyzer", "ContributionElementPartialCodeFixProvider", "SPARK031",
            [FixtureProject.Of("App", ("Song.cs", song), ("Catalog.cs", catalog))],
            referenceTypes: ContributionsGeneratorTests.References(softDelete: true),
            analyzerAssemblyName: Assembly, codeFixAssemblyName: Assembly);

        result.Diagnostics.Should().ContainSingle();
        var fixedCatalog = result.Document("Catalog.cs");
        fixedCatalog.Should().Contain("public static partial class Catalog");
        fixedCatalog.Should().Contain("    public partial class Lyrics");
        result.Document("Song.cs").Should().Be(song.Replace("\r\n", "\n"));
    }

    // ---------------------------------------------------------------- SPARK032 external element

    [Fact]
    public async Task SPARK032_warns_for_an_element_from_another_assembly_and_generates_nothing()
    {
        var library = GeneratorHarness.CompileToMetadataReference("LyricsLibrary", ["""
            using MintPlayer.Spark.Contributions;

            namespace Library;

            public partial class Lyrics
            {
                [ContributionSlot] public string Language { get; set; } = "";
                public string Text { get; set; } = "";
            }
            """], ContributionsGeneratorTests.References(softDelete: true));

        var analyzer = (DiagnosticAnalyzer)GeneratorHarness.InstantiateComponent("ContributionsAnalyzer", Assembly, typeof(DiagnosticAnalyzer));
        var result = await GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            ContributionsGeneratorTests.GeneratorName, [analyzer],
            [("Fixture.cs", Fixture("[Contribution] [Newtonsoft.Json.JsonIgnore] public List<Library.Lyrics> Lyrics { get; set; } = new();", ""))],
            referenceTypes: ContributionsGeneratorTests.References(softDelete: true), rootNamespace: "TestApp",
            additionalReferences: [library], generatorAssemblyName: Assembly);

        var diagnostic = result.Diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK032");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("LyricsLibrary");
        result.GeneratedTreePaths.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- SPARK033 soft delete

    [Fact]
    public async Task SPARK033_warns_that_contributions_are_hard_deleted_without_SoftDelete()
    {
        var diagnostics = await AnalyzeAsync(ContributionsGeneratorTests.SongSource("ContributionAttribution.None"), softDelete: false);
        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK033");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("SongLyricsContribution");
    }

    // ---------------------------------------------------------------- SPARK034 shape

    [Theory]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public List<Lyrics> Lyrics { get; } = new();", "public partial record Lyrics([property: ContributionSlot] string Language, string Text);")]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public List<Lyrics> Lyrics { get; set; } = new();", "public partial struct Lyrics { [ContributionSlot] public string Language { get; set; } public string Text { get; set; } }")]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public IEnumerable<Lyrics> Lyrics { get; } = new List<Lyrics>();", "public partial class Lyrics { [ContributionSlot] public string Language { get; set; } = \"\"; public string Text { get; set; } = \"\"; }")]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public static List<Lyrics> Lyrics { get; set; } = new();", "public partial class Lyrics { [ContributionSlot] public string Language { get; set; } = \"\"; public string Text { get; set; } = \"\"; }")]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public List<Lyrics> Lyrics { get; set; } = new();", "public partial class Lyrics { [ContributionSlot] public string Language { get; } = \"\"; public string Text { get; set; } = \"\"; }")]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public HashSet<string> Lyrics { get; set; } = new();", "")]
    [InlineData("[Contribution] [Newtonsoft.Json.JsonIgnore] public List<string> Lyrics { get; set; } = new();", "")]
    public async Task SPARK034_refuses_an_unsupported_declaration(string property, string element)
    {
        var diagnostics = await AnalyzeAsync(Fixture(property, element));
        diagnostics.Select(d => d.Id).Should().Contain("SPARK034");
        diagnostics.Where(d => d.Id == "SPARK034").Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SPARK034_accepts_a_get_only_list_filled_in_place()
    {
        var diagnostics = await AnalyzeAsync(Fixture(
            "[Contribution] [Newtonsoft.Json.JsonIgnore] public IList<Lyrics> Lyrics { get; } = new List<Lyrics>();", Lyrics()));
        diagnostics.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- SPARK035 reserved members

    [Theory]
    [InlineData("public string Key { get; set; } = \"\";")]
    [InlineData("public string Id { get; set; } = \"\";")]
    [InlineData("public string ContributorId { get; set; } = \"\";")]
    [InlineData("[MintPlayer.Spark.Abstractions.ValueKey] public string RowId { get; set; } = \"\";")]
    public async Task SPARK035_refuses_a_member_that_clashes_with_generated_code(string member)
    {
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty, Lyrics(values: "public string Text { get; set; } = \"\";\n    " + member)));
        Ids(diagnostics).Should().Equal("SPARK035");
    }

    [Fact]
    public async Task SPARK035_refuses_a_ValueObject_element()
    {
        var diagnostics = await AnalyzeAsync(Fixture(LyricsProperty,
            "[MintPlayer.Spark.Abstractions.ValueObject]\n" + Lyrics()));
        diagnostics.Select(d => d.Id).Should().Contain("SPARK035");
    }
}
