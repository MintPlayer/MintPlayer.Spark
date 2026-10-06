using System.Reflection;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The generator's own JSON reader (<c>MiniJson</c>) and tree flattener, driven through
/// <c>LibraryLayersGenerator</c>, which checks every <c>translations.json</c> the compiler sees, because
/// both are internal to an assembly the tests load rather than reference. The parse error's message is
/// carried in SPARK_TRANS_001, so each malformed shape can be told apart by its text.
/// </summary>
public class TranslationsMiniJsonTests
{
    /// <summary>A plain <c>translations.json</c> (an application's): checked, never embedded.</summary>
    private static GeneratorRunResult Run(string translations) => GeneratorHarness.Run(
        "LibraryLayersGenerator",
        sources: [],
        rootNamespace: "TestApp",
        additionalTexts: [("translations.json", translations)]);

    /// <summary>A library's translations layer: checked, and embedded as written.</summary>
    private static GeneratorRunResult RunLayer(string translations) => GeneratorHarness.Run(
        "LibraryLayersGenerator",
        sources: [],
        referenceTypes: [typeof(SparkLayerAttribute)],
        additionalTexts: [("App_Data/translations.json", translations)],
        globalOptions: new Dictionary<string, string> { ["build_property.SparkLibraryAlias"] = "probe" },
        additionalTextOptions: new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["App_Data/translations.json"] = new Dictionary<string, string> { ["build_metadata.AdditionalFiles.SparkLayerPath"] = "translations.json" },
        });

    [Theory]
    [InlineData("""{ "a": [1] }""", "Arrays are not allowed in translations.json (at 'a')")]
    [InlineData("""{ "a": { "en": true } }""", "Booleans are not allowed in translations.json (at 'a.en')")]
    [InlineData("""{ "a": false }""", "Booleans are not allowed")]
    [InlineData("""{ "a": 1 }""", "Numbers are not allowed in translations.json (at 'a')")]
    [InlineData("""{ "a": -1 }""", "Numbers are not allowed")]
    [InlineData("""{ "a": x }""", "Unexpected character 'x'")]
    [InlineData("""{ "a": nul }""", "Unexpected token")]
    [InlineData("""{ "a" "b" }""", "Expected ':' after property name")]
    [InlineData("""{ a: "b" }""", "Expected property name")]
    [InlineData("""{ "a": "b" """, "Unexpected end of object.")]
    [InlineData("""{ "a": "b" x""", "Expected ',' or '}'")]
    [InlineData("""{ "a": """, "Unexpected end of input.")]
    [InlineData("""{ "a": "b\""", "Unterminated escape sequence.")]
    [InlineData("""{ "a": "\u12""", "Truncated unicode escape.")]
    [InlineData("""{ "a": "\uZZZZ" }""", "Invalid unicode escape '\\uZZZZ'")]
    [InlineData("""{ "a": "\q" }""", "Invalid escape sequence '\\q'")]
    [InlineData("""{ "a": "open""", "Unterminated string.")]
    [InlineData("""{} extra""", "Unexpected trailing content")]
    [InlineData("   ", "Unexpected end of input.")]
    public void Malformed_json_is_reported_with_the_reason(string translations, string reason)
    {
        var result = Run(translations);

        result.GeneratedSources.Should().BeEmpty();
        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK_TRANS_001");
        diagnostic.GetMessage().Should().Contain(reason);
    }

    [Fact]
    public void A_mixed_node_and_an_empty_object_are_reported_and_the_rest_still_emits()
    {
        var result = Run("""
            {
              "good": { "en": "Fine" },
              "mixed": { "en": "Leaf", "child": { "en": "Nested" } },
              "empty": {},
              "nothing": { "en": null }
            }
            """);

        result.GeneratorDiagnostics.Select(d => d.Id).Should().BeEquivalentTo(
            ["SPARK_TRANS_002", "SPARK_TRANS_003", "SPARK_TRANS_002"]);
        result.GeneratorDiagnostics.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("mixed"));
        result.GeneratorDiagnostics.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("empty"));
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    public void A_root_that_is_not_an_object_produces_nothing(string translations)
    {
        var result = Run(translations);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Escapes_survive_the_round_trip_into_the_compiled_attribute()
    {
        // Read back through a compiled assembly: the text goes JSON -> C# literal -> metadata, and a
        // mistake at either step shows up only here.
        var result = RunLayer("""
            { "quote": { "en": "say \"hi\" \\ \/ \b\f\n\r\t café \u0001 \u007f" } }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        var attribute = Compile(result).Single();
        var parsed = System.Text.Json.JsonDocument.Parse(attribute.Json);

        parsed.RootElement.GetProperty("quote").GetProperty("en").GetString()
            .Should().Be("say \"hi\" \\ / \b\f\n\r\t café \u0001 \u007f");
    }

    [Fact]
    public void A_large_file_is_embedded_whole_in_one_attribute()
    {
        // The flattened 60 KB chunks are gone: an attribute blob lives in #Blob, not #US, so its size
        // is not the ceiling CS8103 guards (composition S1 round-tripped 20 MB).
        var sb = new StringBuilder("{");
        for (var i = 0; i < 1200; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"key{i:D4}\": {{ \"en\": \"{new string('x', 60)} {i}\" }}");
        }
        sb.Append('}');

        var attribute = Compile(RunLayer(sb.ToString())).Should().ContainSingle().Which;

        attribute.Json.Should().Be(sb.ToString());
        Encoding.UTF8.GetByteCount(attribute.Json).Should().BeGreaterThan(60 * 1024);
    }

    private static List<SparkLayerAttribute> Compile(GeneratorRunResult result)
    {
        var assembly = GeneratorHarness.EmitAndLoad(
            "TranslationsProbe" + Guid.NewGuid().ToString("N"),
            result.GeneratedSources.Select(s => s.Source),
            referenceTypes: [typeof(SparkLayerAttribute)]);

        return [.. assembly.GetCustomAttributes<SparkLayerAttribute>()];
    }
}
