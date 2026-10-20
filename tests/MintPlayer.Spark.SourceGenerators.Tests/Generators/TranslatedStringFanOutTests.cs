using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// A <c>TranslatedString</c> is a list of tagged entries, and RavenDB cannot usefully sort or search one, so it
/// fans out into one index field per language.
/// <para>
/// It persists as <c>{"Entries":[{"Tag":"nl","Value":"…"}]}</c>, the API's shape too (commerce X17), so the
/// map reads the first entry of exactly that tag:
/// <c>car.Description!.Entries.Where(__entry =&gt; __entry.Tag == "nl").Select(__entry =&gt; __entry.Value).FirstOrDefault()</c>.
/// <c>TranslatedStringPersistedShapeTests</c> runs that path against a live server.
/// </para>
/// <para>A hyphenated tag (<c>zh-Hant</c>) gives the C# identifier <c>Description_zh_Hant</c>; the map compares the
/// tag in canonical case, the case it is stored in.</para>
/// <para>The language set cannot come from DI — a generator has none — so it arrives as an
/// <c>App_Data/culture.json</c> AdditionalFile.</para>
/// </summary>
public class TranslatedStringFanOutTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private const string CultureJson = """
        {
          "languages": ["en", "fr", "nl"],
          "defaultLanguage": "en"
        }
        """;

    private const string TranslatedCar = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Car
        {
            [Search] public TranslatedString? Description { get; set; }
        }
        """;

    private static GeneratorRunResult Run(string source, string? cultureJson = CultureJson)
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            // TranslatedString is listed because the attributes now live in their own assembly, so
            // typeof(GenerateIndexAttribute) alone no longer brings Abstractions into the compilation.
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(TranslatedString), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalTexts: cultureJson is null
                ? null
                : [("/proj/App_Data/culture.json", cultureJson)]);

    [Fact]
    public void One_field_per_language_is_emitted()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;

        generated.Should().Contain("public string? Description_en { get; set; }");
        generated.Should().Contain("public string? Description_fr { get; set; }");
        generated.Should().Contain("public string? Description_nl { get; set; }");
    }

    private const string NlMap = "car.Description!.Entries.Where(__entry => __entry.Tag == \"nl\").Select(__entry => __entry.Value).FirstOrDefault()";

    /// <summary>
    /// The CLR path and the stored JSON path agree here — <c>Entries</c>, <c>Tag</c> and <c>Value</c> are the
    /// stored member names, which RavenDB evaluates natively. <c>GetValue("nl")</c> would deploy happily and
    /// return null forever.
    /// </summary>
    [Fact]
    public void Each_language_field_maps_through_the_entries_of_its_tag()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;

        generated.Should().Contain($"Description_nl = {NlMap},");
        generated.Should().NotContain("GetValue(");
        generated.Should().NotContain("Translations[");
    }

    /// <summary>
    /// X17: <c>zh-Hant</c> used to give <c>Description_zh-Hant</c>, which is not a C# identifier. The field name
    /// turns <c>-</c> into <c>_</c>; the map keeps the real tag, canonicalized (<c>zh-hant</c> → <c>zh-Hant</c>)
    /// because stored tags are canonical and the map compares with <c>==</c>.
    /// </summary>
    [Fact]
    public void A_hyphenated_tag_gives_a_sanitized_field_and_maps_the_canonical_tag()
    {
        var generated = Run(TranslatedCar, cultureJson: """
            {
              "languages": ["en", "zh-hant", "sr-latn-rs"]
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("public string? Description_zh_Hant { get; set; }");
        generated.Should().Contain("Description_zh_Hant = car.Description!.Entries.Where(__entry => __entry.Tag == \"zh-Hant\").Select(__entry => __entry.Value).FirstOrDefault(),");
        generated.Should().Contain("public string? Description_sr_Latn_RS { get; set; }");
        generated.Should().Contain("__entry.Tag == \"sr-Latn-RS\"");
        generated.Should().Contain("public string? Description_zh_HantSearch { get; set; }");
        generated.Should().NotContain("Description_zh-");
    }

    [Fact]
    public void Search_gives_each_language_its_own_indexing_and_companion()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VCar.Description_nlSearch), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
        generated.Should().Contain("public string? Description_nlSearch { get; set; }");
        generated.Should().Contain($"Description_nlSearch = {NlMap},");
    }

    /// <summary>Matches the shape issue #210 asks for: <c>Name_nl</c> visible, <c>Name_nlSearch</c> ignored.</summary>
    [Fact]
    public void Language_fields_stay_in_the_model_and_only_companions_are_ignored()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;
        var lines = generated.Split('\n');

        var languageLine = Array.FindIndex(lines, l => l.Contains("public string? Description_nl {"));
        lines[languageLine - 1].Should().NotContain("IgnoreProperty");

        var companionLine = Array.FindIndex(lines, l => l.Contains("public string? Description_nlSearch {"));
        lines[companionLine - 1].Should().Contain("IgnorePropertyAttribute");
    }

    /// <summary>
    /// The whole-object field is replaced, not kept alongside: a <c>string?</c> named <c>Description</c> beside
    /// the entity's <c>TranslatedString Description</c> is a type mismatch the model merge rejects.
    /// </summary>
    [Fact]
    public void The_whole_object_field_is_not_emitted()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;

        generated.Should().NotContain("Description = car.Description,");
        generated.Should().NotContain("public string? Description {");
    }

    [Fact]
    public void Without_Search_the_language_fields_get_no_indexing_or_companions()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public TranslatedString? Description { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Description_nl");
        generated.Should().NotContain("Description_nlSearch");
        generated.Should().NotContain("nameof(VCar.Description_nl)");
    }

    /// <summary>
    /// Falls back to the one language <c>CultureLoader</c> itself falls back to, so a build without the file
    /// generates the shape the app would then serve.
    /// </summary>
    [Fact]
    public void An_absent_culture_file_falls_back_to_English_only()
    {
        var generated = Run(TranslatedCar, cultureJson: null).GeneratedSources[0].Source;

        generated.Should().Contain("Description_en");
        generated.Should().NotContain("Description_fr");
        generated.Should().NotContain("Description_nl");
    }

    [Fact]
    public void A_malformed_culture_file_falls_back_rather_than_failing_the_build()
    {
        var result = Run(TranslatedCar, cultureJson: "{ this is not json");

        result.GeneratedSources.Should().ContainSingle();
        result.GeneratedSources[0].Source.Should().Contain("Description_en");
    }

    [Fact]
    public void Language_order_follows_the_culture_file()
    {
        var generated = Run(TranslatedCar, cultureJson: """
            {
              "languages": ["nl", "en"]
            }
            """).GeneratedSources[0].Source;

        generated.IndexOf("Description_nl {", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
        generated.IndexOf("Description_nl {", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("Description_en {", StringComparison.Ordinal));
    }
}
