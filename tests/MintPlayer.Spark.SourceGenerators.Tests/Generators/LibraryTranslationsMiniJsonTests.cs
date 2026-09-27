using System.Reflection;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The generator's own JSON reader (<c>MiniJson</c>) and tree flattener, driven through
/// <c>LibraryTranslationsGenerator</c> because both are internal to an assembly the tests load
/// rather than reference. The parse error's message is carried in SPARK_TRANS_001, so each malformed
/// shape can be told apart by its text.
/// </summary>
public class LibraryTranslationsMiniJsonTests
{
    private static GeneratorRunResult Run(string translations) => GeneratorHarness.Run(
        "LibraryTranslationsGenerator",
        sources: [],
        rootNamespace: "TestApp",
        additionalTexts: [("translations.json", translations)]);

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
        Chunks(result).Should().ContainSingle().Which.Should().Contain("\"good\"");
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
        // Read back through a compiled assembly: the text goes JSON -> MiniJson -> JSON -> C# literal
        // -> metadata, and a mistake at any step shows up only here.
        var result = Run("""
            { "quote": { "en": "say \"hi\" \\ \/ \b\f\n\r\t café \u0001 \u007f" } }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        var attribute = Compile(result).Single();
        var parsed = System.Text.Json.JsonDocument.Parse(attribute.Json);

        parsed.RootElement.GetProperty("quote").GetProperty("en").GetString()
            .Should().Be("say \"hi\" \\ / \b\f\n\r\t café \u0001 \u007f");
    }

    [Fact]
    public void A_file_beyond_the_attribute_ceiling_is_split_into_chunks_that_lose_nothing()
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < 1200; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"key{i:D4}\": {{ \"en\": \"{new string('x', 60)} {i}\" }}");
        }
        sb.Append('}');

        var attributes = Compile(Run(sb.ToString()));

        attributes.Count.Should().BeGreaterThan(1, "roughly 90 KB of entries cannot fit one 60 KB chunk");
        attributes.Select(a => a.ChunkCount).Should().OnlyContain(c => c == attributes.Count);
        attributes.Select(a => a.ChunkIndex).Should().BeEquivalentTo(Enumerable.Range(0, attributes.Count));
        attributes.Should().OnlyContain(a => Encoding.UTF8.GetByteCount(a.Json) <= 60 * 1024 + 256);
        var keys = attributes
            .SelectMany(a => System.Text.Json.JsonDocument.Parse(a.Json).RootElement.EnumerateObject().Select(p => p.Name))
            .ToList();
        keys.Should().HaveCount(1200);
        keys.Distinct().Should().HaveCount(1200, "an entry must land in exactly one chunk");
    }

    private static IEnumerable<string> Chunks(GeneratorRunResult result) =>
        Compile(result).Select(a => a.Json);

    private static List<SparkTranslationsAttribute> Compile(GeneratorRunResult result)
    {
        var assembly = GeneratorHarness.EmitAndLoad(
            "TranslationsProbe" + Guid.NewGuid().ToString("N"),
            result.GeneratedSources.Select(s => s.Source),
            referenceTypes: [typeof(SparkTranslationsAttribute)]);

        return [.. assembly.GetCustomAttributes<SparkTranslationsAttribute>().OrderBy(a => a.ChunkIndex)];
    }
}
