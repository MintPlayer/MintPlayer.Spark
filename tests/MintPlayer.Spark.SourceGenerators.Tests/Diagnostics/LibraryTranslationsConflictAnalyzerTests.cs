using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK_TRANS_005 (#467, D3): two referenced LIBRARIES translate the same (key, language)
/// differently. Composition M5 (D10) moved it from the removed host aggregator into an analyzer that
/// composes the library layers and the app's own <c>translations.json</c> with the run time's engine.
/// The app overriding a library is the intended way to customize and is silent; so is a library
/// overriding a library it depends on (grill Q3). How the texts compose is tested at run time
/// (<c>SparkTranslationLayersTests</c>).
/// </summary>
public class LibraryTranslationsConflictAnalyzerTests
{
    private const string AnalyzerName = "LibraryTranslationsConflictAnalyzer";

    private static MetadataReference Library(string name, string json, params string[] dependsOn)
    {
        var source = $"[assembly: MintPlayer.Spark.Abstractions.SparkLayer({SymbolDisplay.FormatLiteral(name.ToLowerInvariant(), quote: true)}, \"translations\", \"translations.json\", {SymbolDisplay.FormatLiteral(json, quote: true)})]";
        if (dependsOn.Length > 0)
            source += $"\n[assembly: MintPlayer.Spark.Abstractions.SparkLayerDependencies({string.Join(", ", dependsOn.Select(d => SymbolDisplay.FormatLiteral(d, quote: true)))})]";
        return GeneratorHarness.CompileToMetadataReference(name, [source], [typeof(SparkLayerAttribute)]);
    }

    private static Task<IReadOnlyList<Diagnostic>> RunAsync(string? appTranslations, OutputKind outputKind, params MetadataReference[] libraries)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["public static class Program { public static void Main() { } }"],
            referenceTypes: [typeof(SparkLayerAttribute)],
            additionalTexts: appTranslations is null ? [] : [("App_Data/translations.json", appTranslations)],
            additionalReferences: libraries,
            outputKind: outputKind);

    private static Task<IReadOnlyList<Diagnostic>> RunHostAsync(string? appTranslations, params MetadataReference[] libraries)
        => RunAsync(appTranslations, OutputKind.ConsoleApplication, libraries);

    [Fact]
    public async Task Two_libraries_with_different_values_for_the_same_key_and_language_warn()
    {
        var diagnostics = await RunHostAsync(null,
            Library("LibA", """{"save":{"en":"Save","nl":"Opslaan"}}"""),
            Library("LibB", """{"save":{"en":"Store","fr":"Enregistrer"}}"""));

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK_TRANS_005");
        diagnostic.GetMessage().Should().Contain("'LibB' and 'LibA'").And.Contain("'save'").And.Contain("'en'");
    }

    [Fact]
    public async Task A_nested_layer_and_a_dotted_layer_meet_on_the_same_key()
    {
        var diagnostics = await RunHostAsync(null,
            Library("LibA", """{"actions":{"save":{"en":"Save"}},"$schema":"x"}"""),
            Library("LibB", """{"actions.save":{"en":"Store"}}"""));

        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'actions.save'");
    }

    [Fact]
    public async Task A_library_overrides_a_library_it_depends_on_silently_whatever_their_names()
    {
        // Composition D2 / grill Q3: AExt stacks above ZLib because it depends on it, although it sorts
        // first by name, and overriding a dependency is intended, never SPARK_TRANS_005.
        var diagnostics = await RunHostAsync(null,
            Library("AExt", """{"save":{"en":"Store"}}""", "ZLib"),
            Library("ZLib", """{"save":{"en":"Save","nl":"Opslaan"}}"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_libraries_agreeing_on_a_value_or_on_different_languages_do_not_warn()
    {
        var diagnostics = await RunHostAsync(null,
            Library("LibA", """{"save":{"en":"Save","nl":"Opslaan"}}"""),
            Library("LibB", """{"save":{"en":"Save","fr":"Enregistrer"}}"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task The_app_overriding_a_library_is_silent()
    {
        var diagnostics = await RunHostAsync("""{ "save": { "en": "Keep", "nl": "" } }""",
            Library("LibA", """{"save":{"en":"Save","nl":"Opslaan"}}"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_library_removing_a_namespace_another_library_ships_is_no_conflict()
    {
        var diagnostics = await RunHostAsync(null,
            Library("LibA", """{"moderation":{"title":{"en":"Moderation"}}}"""),
            Library("LibB", """{"moderation":null}"""),
            Library("LibC", """{"moderation":{"title":{"en":"Review"}}}"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_app_file_that_does_not_read_still_lets_the_libraries_be_checked()
    {
        // SPARK_TRANS_001 reports the app's file; the conflict between the libraries is still found.
        var diagnostics = await RunHostAsync("{ not json",
            Library("LibA", """{"save":{"en":"Save"}}"""),
            Library("LibB", """{"save":{"en":"Store"}}"""));

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK_TRANS_005");
    }

    [Fact]
    public async Task A_library_compilation_is_not_analyzed()
    {
        // As the aggregator was: only an application composes translations.
        var diagnostics = await RunAsync(null, OutputKind.DynamicallyLinkedLibrary,
            Library("LibA", """{"save":{"en":"Save"}}"""),
            Library("LibB", """{"save":{"en":"Store"}}"""));

        diagnostics.Should().BeEmpty();
    }
}
