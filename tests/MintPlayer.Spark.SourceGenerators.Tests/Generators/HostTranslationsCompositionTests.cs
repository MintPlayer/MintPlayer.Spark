using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using RunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// #467 D2/D3/D23: translations compose per (key, language). Libraries apply in layer order (core,
/// then by dependency, alphabetically between unrelated ones; composition D2), the app last; a layer
/// replaces only the languages it defines. Only two LIBRARIES disagreeing on the same
/// (key, language) is reported (SPARK_TRANS_005); the app overriding a library is the intended way to
/// customize and is silent. An app's <c>""</c> counts as "not defined".
/// </summary>
public class HostTranslationsCompositionTests
{
    private const string GeneratorName = "HostTranslationsAggregatorGenerator";

    private static MetadataReference Library(string name, string json, params string[] dependsOn)
    {
        var escaped = json.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var source = $"using MintPlayer.Spark.Abstractions;\n[assembly: SparkLayer(\"{name.ToLowerInvariant()}\", \"translations\", \"translations.json\", \"{escaped}\")]";
        if (dependsOn.Length > 0)
            source += $"\n[assembly: SparkLayerDependencies({string.Join(", ", dependsOn.Select(d => $"\"{d}\""))})]";
        return GeneratorHarness.CompileToMetadataReference(
            assemblyName: name,
            sources: [source],
            referenceTypes: [typeof(SparkLayerAttribute)]);
    }

    private static RunResult RunHost(string? hostTranslations, params MetadataReference[] libraries)
        => GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            referenceTypes: [typeof(SparkLayerAttribute)],
            rootNamespace: "TestApp",
            additionalTexts: hostTranslations is null ? [] : [("translations.json", hostTranslations)],
            outputKind: OutputKind.ConsoleApplication,
            additionalReferences: libraries);

    private static string Registry(RunResult result)
        => result.GeneratedSources.Single(s => s.HintName.Contains("SparkTranslationsRegistry")).Source;

    private static string Line(string registry, string key)
        => registry.Split('\n').Single(l => l.Contains($"d[\"{key}\"]")).Trim();

    private const string LibraryEnFrNl = """{"save":{"en":"Save","fr":"Enregistrer","nl":"Opslaan"}}""";

    [Fact]
    public void An_app_adding_a_language_keeps_the_library_languages_and_appends_its_own()
    {
        var result = RunHost("""{ "save": { "es": "Guardar" } }""", Library("Lib", LibraryEnFrNl));

        Line(Registry(result), "save").Should().Contain(
            "[\"en\"] = \"Save\", [\"fr\"] = \"Enregistrer\", [\"nl\"] = \"Opslaan\", [\"es\"] = \"Guardar\"");
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_TRANS_005");
    }

    [Fact]
    public void An_app_overriding_one_language_keeps_the_others_in_their_order()
    {
        var result = RunHost("""{ "save": { "nl": "Bewaren" } }""", Library("Lib", LibraryEnFrNl));

        Line(Registry(result), "save").Should().Contain(
            "[\"en\"] = \"Save\", [\"fr\"] = \"Enregistrer\", [\"nl\"] = \"Bewaren\"");
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_TRANS_005");
    }

    [Fact]
    public void An_empty_app_value_does_not_blank_the_library_value()
    {
        var result = RunHost("""{ "save": { "nl": "", "es": "" } }""", Library("Lib", LibraryEnFrNl));

        var line = Line(Registry(result), "save");
        line.Should().Contain("[\"nl\"] = \"Opslaan\"");
        line.Should().NotContain("\"es\"");
    }

    [Fact]
    public void An_app_key_with_only_empty_values_is_not_emitted()
    {
        var result = RunHost("""{ "draft": { "en": "" } }""");

        Registry(result).Should().NotContain("d[\"draft\"]");
    }

    [Fact]
    public void Two_libraries_with_different_values_for_the_same_key_and_language_warn()
    {
        var result = RunHost(null,
            Library("LibA", """{"save":{"en":"Save","nl":"Opslaan"}}"""),
            Library("LibB", """{"save":{"en":"Store","fr":"Enregistrer"}}"""));

        var conflicts = result.GeneratorDiagnostics.Where(d => d.Id == "SPARK_TRANS_005").ToList();
        conflicts.Should().ContainSingle();
        var message = conflicts[0].GetMessage();
        message.Should().Contain("'save'").And.Contain("'en'").And.Contain("LibA").And.Contain("LibB");
        Line(Registry(result), "save").Should().Contain(
            "[\"en\"] = \"Store\", [\"nl\"] = \"Opslaan\", [\"fr\"] = \"Enregistrer\"");
    }

    [Fact]
    public void A_library_overrides_a_library_it_depends_on_silently_whatever_their_names()
    {
        // Composition D2 / grill Q3: AExt stacks above ZLib because it depends on it, although it sorts
        // first by name, and overriding a dependency is intended, never SPARK_TRANS_005.
        var result = RunHost(null,
            Library("AExt", """{"save":{"en":"Store"}}""", "ZLib"),
            Library("ZLib", """{"save":{"en":"Save","nl":"Opslaan"}}"""));

        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_TRANS_005");
        Line(Registry(result), "save").Should().Contain("[\"en\"] = \"Store\", [\"nl\"] = \"Opslaan\"");
    }

    [Fact]
    public void A_nested_library_layer_is_flattened_before_it_composes()
    {
        var result = RunHost(null, Library("Lib", """{"actions":{"save":{"en":"Save"}},"$schema":"x"}"""));

        Line(Registry(result), "actions.save").Should().Contain("[\"en\"] = \"Save\"");
    }

    [Fact]
    public void Two_libraries_agreeing_on_a_value_or_on_different_languages_do_not_warn()
    {
        var result = RunHost(null,
            Library("LibA", """{"save":{"en":"Save","nl":"Opslaan"}}"""),
            Library("LibB", """{"save":{"en":"Save","fr":"Enregistrer"}}"""));

        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_TRANS_005");
    }
}
