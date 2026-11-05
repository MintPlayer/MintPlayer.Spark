using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// #238 M3 (<c>docs/spark_search_PRD.md</c> D5): an application that references MintPlayer.Spark.Search gets one
/// <c>Spark_Search</c> map per type whose model says <c>"search": { "enabled": true }</c>, over its string and
/// string-collection fields only.
/// </summary>
public class SearchIndexGeneratorTests
{
    private const string GeneratorName = "SearchIndexGenerator";

    private static readonly Type[] SearchReferences =
    [
        typeof(MintPlayer.Spark.Search.SparkSearchExtensions),
        typeof(MintPlayer.Spark.Abstractions.Search.SparkSearchEntry),
        typeof(MintPlayer.Spark.SparkMultiMapIndexCreationTask<>),
        typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
    ];

    private const string MusicSource = """
        namespace Music;
        public class Song
        {
            public string? Id { get; set; }
            public string Title { get; set; } = "";
            public string[]? Tags { get; set; }
            public System.Collections.Generic.List<string> Writers { get; set; } = new();
            public int Year { get; set; }
            public bool? IsDeleted { get; set; }
        }
        public class Artist
        {
            public string? Id { get; set; }
            public string Name { get; set; } = "";
            public string Country { get; set; } = "";
        }
        public class Label
        {
            public string? Id { get; set; }
            public string Name { get; set; } = "";
        }
        """;

    private static (string Path, string Text) Model(string name, string search, string breadcrumb = "{Name}")
        => ($"App_Data/Model/{name}.json",
            $$"""{ "persistentObject": { "name": "{{name}}", "clrType": "Music.{{name}}", "breadcrumb": "{{breadcrumb}}", "search": {{search}} } }""");

    private static GeneratorRunResult Run(OutputKind outputKind = OutputKind.ConsoleApplication, Type[]? references = null, string source = MusicSource, params (string Path, string Text)[] models)
        => GeneratorHarness.Run(
            GeneratorName,
            sources: [source],
            referenceTypes: references ?? SearchReferences,
            rootNamespace: "MusicApp",
            additionalTexts: models,
            outputKind: outputKind);

    /// <summary>The final compilation's errors; an app fixture has no Main (CS5001).</summary>
    private static IEnumerable<Diagnostic> Errors(GeneratorRunResult result)
        => result.FinalCompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001");

    private static string Index(GeneratorRunResult result)
        => result.GeneratedSources.Single(s => s.HintName.StartsWith("Spark_Search", StringComparison.Ordinal)).Source;

    private static (string Path, string Text)[] MusicModels() =>
    [
        Model("Song", """{ "enabled": true, "fields": ["Title", "Tags", "Writers", "Year"], "fuzzy": true }""", "{Title}"),
        Model("Artist", """{ "enabled": true }"""),
        Model("Label", """{ "enabled": false }"""),
    ];

    [Fact]
    public void Every_enabled_type_gets_a_map_over_its_text_fields_and_the_index_compiles()
    {
        var result = Run(models: MusicModels());

        result.GeneratorDiagnostics.Should().BeEmpty();
        Errors(result).Should().BeEmpty();
        var index = Index(result);
        index.Should().Contain("namespace MusicApp.Indexes").And.Contain("class Spark_Search");
        index.Should().Contain("AddMap<global::Music.Song>").And.Contain("AddMap<global::Music.Artist>").And.NotContain("global::Music.Label");
        index.Should().Contain("(d.Title ?? \"\")")
            .And.Contain("string.Join(\" \", d.Tags)")
            .And.Contain("string.Join(\" \", d.Writers)")
            .And.NotContain("d.Year");
        index.Should().Contain("Configuration[\"Indexing.Static.SearchEngineType\"] = \"Lucene\"");
    }

    [Fact]
    public void Without_search_fields_the_breadcrumb_placeholders_are_searched()
    {
        var index = Index(Run(models: MusicModels()));

        // Artist has no "fields" and no "label": its breadcrumb "{Name}" decides; Country is not searched.
        var artistMap = index[index.IndexOf("AddMap<global::Music.Artist>", StringComparison.Ordinal)..];
        artistMap = artistMap[..artistMap.IndexOf("});", StringComparison.Ordinal)];
        artistMap.Should().Contain("d.Name").And.NotContain("d.Country").And.Contain("IsDeleted = false");
    }

    [Fact]
    public void Search_fields_add_to_the_label_attributes_and_never_replace_them()
    {
        // Resolve finds a label's object through this text: without Name, "Queen" could never resolve.
        var index = Index(Run(models: [Model("Artist", """{ "enabled": true, "label": "{Name}", "fields": ["Country"] }""")]));

        index.Should().Contain("(d.Name ?? \"\") + \" \" + (d.Country ?? \"\")");
    }

    [Fact]
    public void A_soft_deletable_type_maps_its_flag()
    {
        var index = Index(Run(models: MusicModels()));

        var songMap = index[index.IndexOf("AddMap<global::Music.Song>", StringComparison.Ordinal)..];
        songMap[..songMap.IndexOf("});", StringComparison.Ordinal)].Should().Contain("IsDeleted = d.IsDeleted == true");
    }

    [Fact]
    public void A_library_gets_no_index()
        => Run(OutputKind.DynamicallyLinkedLibrary, models: MusicModels()).GeneratedSources
            .Where(s => s.HintName.StartsWith("Spark_Search", StringComparison.Ordinal) && s.Source.Contains("class Spark_Search"))
            .Should().BeEmpty("indexes live only in the application");

    [Fact]
    public void An_application_without_the_search_package_gets_no_index()
        => Run(references: [.. SearchReferences.Skip(1)], models: MusicModels()).GeneratedSources
            .Where(s => s.Source.Contains("class Spark_Search"))
            .Should().BeEmpty();

    [Fact]
    public void A_type_without_text_fields_is_left_out()
    {
        var result = Run(models: [Model("Song", """{ "enabled": true, "fields": ["Year", "Missing"] }""")]);

        result.GeneratedSources.Where(s => s.Source.Contains("class Spark_Search")).Should().BeEmpty();
    }

    // ---- The index the search tests deploy -------------------------------------------------------------

    private const string SearchTestsSource = """
        namespace MintPlayer.Spark.Tests.Search;
        public class SrchSong
        {
            public string? Id { get; set; }
            public string Title { get; set; } = "";
            public bool Hidden { get; set; }
        }
        """;

    /// <summary>
    /// The generator's output for <c>MintPlayer.Spark.Tests</c>' <c>SrchSong</c> fixture. That project is a library, so
    /// the generator never runs there: <c>Search/RavenSearchProviderTests</c> compiles this snapshot and deploys it to a
    /// real server instead, which is how the generated index is shown to compile on RavenDB.
    /// </summary>
    [Fact]
    public Task The_search_tests_index()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            sources: [SearchTestsSource],
            referenceTypes: SearchReferences,
            rootNamespace: "SrchGenerated",
            additionalTexts:
            [
                ("App_Data/Model/SrchSong.json",
                 """{ "persistentObject": { "name": "SrchSong", "clrType": "MintPlayer.Spark.Tests.Search.SrchSong", "breadcrumb": "{Title}", "search": { "enabled": true, "fuzzy": true } } }"""),
            ],
            outputKind: OutputKind.ConsoleApplication);

        Errors(result).Should().BeEmpty();
        return Verifier.Verify(Index(result));
    }
}
