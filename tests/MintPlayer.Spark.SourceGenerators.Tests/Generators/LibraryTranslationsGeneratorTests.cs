using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class LibraryTranslationsGeneratorTests
{
    private const string GeneratorName = "LibraryTranslationsGenerator";

    [Fact]
    public void Well_formed_translations_produce_SparkTranslationsAttribute_emissions()
    {
        var translations = """
            {
              "greeting": { "en": "Hello", "nl": "Hallo" },
              "farewell": { "en": "Bye", "nl": "Tot ziens" }
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            rootNamespace: "TestApp",
            additionalTexts: [("translations.json", translations)]);

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("[assembly: global::MintPlayer.Spark.Abstractions.SparkTranslationsAttribute(");
        generated.Should().Contain("greeting").And.Contain("farewell");
        generated.Should().Contain("Hello").And.Contain("Hallo");
    }

    [Fact]
    public void Non_translations_json_additional_files_are_ignored()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            rootNamespace: "TestApp",
            additionalTexts: [("other.json", "{\"something\": \"value\"}")]);

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_json_reports_a_diagnostic_and_emits_no_source()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            rootNamespace: "TestApp",
            additionalTexts: [("translations.json", "{ not: valid json")]);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().Contain(d =>
            d.Id.StartsWith("SPARK_TRANS"));
    }

    [Fact]
    public void Empty_translations_json_produces_no_source_and_no_diagnostics()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            rootNamespace: "TestApp",
            additionalTexts: [("translations.json", "")]);

        result.GeneratedSources.Should().BeEmpty();
    }
}
