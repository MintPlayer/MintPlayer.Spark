using System.Reflection;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The shared reader (<c>SparkJson</c>) and translations flattener (<c>SparkTranslationLayers</c>),
/// driven through <c>LibraryLayersGenerator</c>, which checks every <c>translations.json</c> the compiler
/// sees, because both are internal to an assembly the tests load rather than reference. Composition M5
/// (D10) replaced the generators' own MiniJson with them, so a file the generator accepts is one the run
/// time composes. The parse error's message is carried in SPARK_TRANS_001, so each malformed shape can be
/// told apart by its text.
/// </summary>
public class TranslationsReaderTests
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
    [InlineData("""{ "a": x }""", "unexpected character 'x'")]
    [InlineData("""{ "a": nul }""", "unexpected token")]
    [InlineData("""{ "a" "b" }""", "expected ':' after a property name")]
    [InlineData("""{ a: "b" }""", "expected a property name")]
    [InlineData("""{ "a": "b" """, "unexpected end of input in an object")]
    [InlineData("""{ "a": "b" x""", "expected ',' or '}'")]
    [InlineData("""{ "a": """, "unexpected end of input")]
    [InlineData("""{ "a": "\uZZZZ" }""", "invalid \\u escape")]
    [InlineData("""{ "a": "\q" }""", "invalid escape '\\q'")]
    [InlineData("""{ "a": "open""", "unterminated string")]
    [InlineData("""{} extra""", "unexpected content after the JSON value")]
    [InlineData("""{ "a": { "en": "x" }, "a": { "nl": "y" } }""", "is stated twice")]
    [InlineData("\"just a string\"", "must be a JSON object")]
    [InlineData("   ", "unexpected end of input")]
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
              "number": { "en": 1 },
              "list": { "en": ["a"] }
            }
            """);

        result.GeneratorDiagnostics.Select(d => d.Id).Should().BeEquivalentTo(
            ["SPARK_TRANS_002", "SPARK_TRANS_003", "SPARK_TRANS_002", "SPARK_TRANS_004"]);
        result.GeneratorDiagnostics.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("'mixed'"));
        result.GeneratorDiagnostics.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("'empty'"));
        result.GeneratorDiagnostics.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("'list.en'"));
    }

    [Fact]
    public void A_null_namespace_is_a_removal_not_a_problem()
    {
        // Composition D3: "ns": null removes a namespace a library ships; it was SPARK_TRANS_002 before M5.
        var result = Run("""{ "moderation": null, "app": { "old": null, "title": { "en": "T" } } }""");

        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void One_key_stated_dotted_and_nested_is_SPARK_TRANS_006()
    {
        var result = Run("""{ "app.title": { "en": "A" }, "app": { "title": { "en": "B" } } }""");

        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK_TRANS_006");
        diagnostic.GetMessage().Should().Contain("'app.title'");
    }

    [Fact]
    public void A_null_root_produces_nothing()
    {
        var result = Run("null");

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
