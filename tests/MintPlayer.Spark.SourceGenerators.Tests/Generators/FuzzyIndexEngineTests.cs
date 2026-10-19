using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;
using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Commerce X18 and X20 item 4 in <c>GenerateIndexGenerator</c>: a <c>[Fuzzy]</c> field puts the generated index on
/// Lucene (Corax refuses fuzzy queries), every other index keeps the server default, and a <c>record</c> entity is
/// seen at all (the syntax filter used to match <c>class</c> only).
/// </summary>
public class FuzzyIndexEngineTests
{
    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(
            "GenerateIndexGenerator",
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(FuzzyAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp", outputKind: OutputKind.ConsoleApplication);

    private static IReadOnlyList<Diagnostic> ErrorsIn(GeneratorRunResult result, string hintName)
        => result.FinalCompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree?.FilePath.EndsWith(hintName, StringComparison.Ordinal) == true)
            .ToList();

    [Fact]
    public void A_fuzzy_field_sets_Lucene_on_the_generated_index_and_marks_the_projection()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Place
            {
                public string? Id { get; set; }
                [Fuzzy] public string Name { get; set; } = string.Empty;
                public int Population { get; set; }
            }
            """);

        var (hintName, generated) = result.GeneratedSources.Single();

        generated.Should().Contain("SearchEngineType = global::Raven.Client.Documents.Indexes.SearchEngineType.Lucene;");
        // The marker travels to the projection, where SPARK063 and Spark's registration read it.
        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.FuzzyAttribute]");
        // Set before the hand-written seam, so a partial OnInitialize has the last word.
        generated.IndexOf("SearchEngineType =", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("OnInitialize();", StringComparison.Ordinal));
        ErrorsIn(result, hintName).Should().BeEmpty();
    }

    [Fact]
    public void An_index_without_a_fuzzy_field_keeps_the_server_default_engine()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Place
            {
                public string? Id { get; set; }
                [Search] public string Name { get; set; } = string.Empty;
            }
            """).GeneratedSources.Single().Source;

        // No pin at all: pinning Corax would change every existing index's definition and rebuild it.
        generated.Should().NotContain("SearchEngineType");
    }

    [Fact]
    public void A_fuzzy_marker_on_a_non_text_property_is_reported()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Place
            {
                public string? Id { get; set; }
                [Fuzzy] public int Population { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK_INDEX_014")
            .Which.GetMessage().Should().Contain("Population");
        // The marker still states what the author queries, so the engine still follows it.
        result.GeneratedSources.Single().Source.Should().Contain("SearchEngineType.Lucene");
    }

    [Fact]
    public void A_fuzzy_translated_string_marks_every_language_field()
    {
        var result = GeneratorHarness.Run(
            "GenerateIndexGenerator",
            ["""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Place
            {
                public string? Id { get; set; }
                [Fuzzy] public TranslatedString? Name { get; set; }
            }
            """],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(FuzzyAttribute), typeof(TranslatedString), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp", outputKind: OutputKind.ConsoleApplication,
            additionalTexts: [("/proj/App_Data/culture.json", """{ "languages": ["en", "nl"], "defaultLanguage": "en" }""")]);

        var generated = result.GeneratedSources.Single().Source;
        generated.Should().Contain("SearchEngineType.Lucene");
        var lines = generated.Split('\n').Select(l => l.Trim()).ToList();
        foreach (var field in new[] { "Name_en", "Name_nl" })
        {
            var declaration = lines.FindIndex(l => l.StartsWith($"public string? {field} ", StringComparison.Ordinal));
            declaration.Should().BeGreaterThan(0);
            lines[declaration - 1].Should().Be("[global::MintPlayer.Spark.Abstractions.FuzzyAttribute]");
        }
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_INDEX_014");
    }

    [Fact]
    public void A_record_entity_gets_its_index()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public record Country
            {
                public string? Id { get; set; }
                public string Code { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public sealed record class Currency(string? Id, string Code);
            """);

        var (hintName, generated) = result.GeneratedSources.Single();

        generated.Should().Contain("public partial class Countries_Overview : global::Raven.Client.Documents.Indexes.AbstractIndexCreationTask<global::TestApp.Entities.Country>");
        generated.Should().Contain("public partial class Currencies_Overview : global::Raven.Client.Documents.Indexes.AbstractIndexCreationTask<global::TestApp.Entities.Currency>");
        generated.Should().Contain("Code = country.Code,");
        // A record's compiler-generated EqualityContract is protected, so it never becomes a field.
        generated.Should().NotContain("EqualityContract");
        ErrorsIn(result, hintName).Should().BeEmpty();
    }

    [Fact]
    public void A_record_struct_is_not_an_entity()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public record struct Coordinates(double Lat, double Lng);
            """);

        result.GeneratedSources.Should().BeEmpty();
    }
}
