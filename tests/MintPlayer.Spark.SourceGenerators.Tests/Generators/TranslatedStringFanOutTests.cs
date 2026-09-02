using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// A <c>TranslatedString</c> is a dictionary, and RavenDB cannot usefully sort or search one, so it fans out
/// into one index field per language.
/// <para>
/// It persists <em>nested</em> — <c>Description.Translations.nl</c> — because its flat
/// <c>{"en":..,"nl":..}</c> converter is System.Text.Json and applies only on the wire, while RavenDB persists
/// through Newtonsoft. So <c>car.Description!.Translations["nl"]</c> is the correct map expression, measured
/// against a live server rather than assumed.
/// </para>
/// <para>The language set cannot come from DI — a generator has none — so it arrives as an
/// <c>App_Data/culture.json</c> AdditionalFile.</para>
/// </summary>
public class TranslatedStringFanOutTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private const string CultureJson = """
        {
          "languages": {
            "en": { "en": "English" },
            "fr": { "en": "French" },
            "nl": { "en": "Dutch" }
          },
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
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp",
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

    /// <summary>
    /// The CLR path and the stored JSON path agree here — the dictionary indexer is what RavenDB evaluates
    /// natively. <c>GetValue("nl")</c> would deploy happily and return null forever.
    /// </summary>
    [Fact]
    public void Each_language_field_maps_through_the_dictionary_indexer()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;

        generated.Should().Contain("Description_nl = car.Description!.Translations[\"nl\"],");
        generated.Should().NotContain("GetValue(");
    }

    [Fact]
    public void Search_gives_each_language_its_own_indexing_and_companion()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VCar.Description_nl), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
        generated.Should().Contain("public string? Description_nlSort { get; set; }");
        generated.Should().Contain("Description_nlSort = car.Description!.Translations[\"nl\"],");
    }

    /// <summary>Matches the shape issue #210 asks for: <c>Name_nl</c> visible, <c>Name_nlSort</c> ignored.</summary>
    [Fact]
    public void Language_fields_stay_in_the_model_and_only_companions_are_ignored()
    {
        var generated = Run(TranslatedCar).GeneratedSources[0].Source;
        var lines = generated.Split('\n');

        var languageLine = Array.FindIndex(lines, l => l.Contains("public string? Description_nl {"));
        lines[languageLine - 1].Should().NotContain("IgnoreProperty");

        var companionLine = Array.FindIndex(lines, l => l.Contains("public string? Description_nlSort {"));
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
        generated.Should().NotContain("Description_nlSort");
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
              "languages": {
                "nl": { "en": "Dutch" },
                "en": { "en": "English" }
              }
            }
            """).GeneratedSources[0].Source;

        generated.IndexOf("Description_nl {", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("Description_en {", StringComparison.Ordinal));
    }
}
