using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

// Hand-written rows standing in for what AttributeDescriptionsGenerator emits: this project does not
// run the Spark analyzers, and the seeding contract is pinned independently of the generator anyway
// (its own snapshot tests live in MintPlayer.Spark.SourceGenerators.Tests; the DemoApp/HR model
// files show the two wired together). DEBUG is defined for test builds, so the rows survive
// [Conditional].
[assembly: SparkAttributeDescription(typeof(MintPlayer.Spark.Tests.Services.MSD_Widget), "Notes", "From the summary.")]
[assembly: SparkAttributeDescription(typeof(MintPlayer.Spark.Tests.Services.MSD_Widget), "Title", "From the summary, loses.")]

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #348, moved by #467 (D5): how C# text becomes an attribute's description on synchronize, and who
/// owns it afterwards. The C# summary is a SEED: it fills <c>en</c> in the app's translations.json
/// when no layer defines the description key, and translations.json owns the value from then on.
/// </summary>
public sealed class ModelSynchronizerDescriptionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog _indexCatalog = Substitute.For<IIndexCatalog>();
    private readonly string _modelPath;

    public ModelSynchronizerDescriptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-modelsync-desc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _modelPath = Path.Combine(_tempDir, "App_Data", "Model");
        _hostEnv.ContentRootPath.Returns(_tempDir);
        _indexCatalog.GetAllEntries().Returns([]);
        _indexCatalog.GetDefaultForCollectionType(Arg.Any<Type>()).Returns((IndexCatalogEntry?)null);
        _indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ModelSynchronizer CreateSynchronizer() => new(_hostEnv, _indexCatalog);

    private string ModelFile(string entityName) => Path.Combine(_modelPath, $"{entityName}.json");

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private EntityAttributeDefinition Attribute(string name) =>
        Read<EntityTypeFile>(ModelFile("MSD_Widget")).PersistentObject.Attributes.Single(a => a.Name == name);

    private void SeedWidgetFile(string attributesJson)
    {
        Directory.CreateDirectory(_modelPath);
        File.WriteAllText(ModelFile("MSD_Widget"), $$$"""
            {"persistentObject":{"id":"11111111-1111-1111-1111-111111111111",
            "name":"MSD_Widget","clrType":"MintPlayer.Spark.Tests.Services.MSD_Widget",
            "attributes":[{{{attributesJson}}}]}}
            """);
    }

    private (string First, string Second) SyncTwice()
    {
        CreateSynchronizer().SynchronizeModels(typeof(MSD_Context));
        var first = File.ReadAllText(ModelFile("MSD_Widget"));
        CreateSynchronizer().SynchronizeModels(typeof(MSD_Context));
        var second = File.ReadAllText(ModelFile("MSD_Widget"));
        return (first, second);
    }


    private const string TitleKey = "model.MSD_Widget.attributes.Title.description";
    private const string NotesKey = "model.MSD_Widget.attributes.Notes.description";
    private const string PlainKey = "model.MSD_Widget.attributes.Plain.description";

    private string TranslationsFile => Path.Combine(_tempDir, "App_Data", "translations.json");

    private void WriteTranslations(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TranslationsFile)!);
        File.WriteAllText(TranslationsFile, json);
    }

    /// <summary>The node at a dotted key, found nested or dotted, as the seeder finds it.</summary>
    private JsonNode? Translation(string key)
    {
        if (!File.Exists(TranslationsFile)) return null;
        return Find(JsonNode.Parse(File.ReadAllText(TranslationsFile))!.AsObject(), key);

        static JsonNode? Find(JsonObject node, string key)
        {
            foreach (var (name, child) in node)
            {
                if (name == key) return child;
                if (child is JsonObject obj && key.StartsWith(name + ".") && Find(obj, key[(name.Length + 1)..]) is { } found)
                    return found;
            }
            return null;
        }
    }

    private string? En(string key) => Translation(key)?["en"]?.GetValue<string>();

    // ── #467 D5: the seed is written into the app's translations.json ───────────────────────────

    [Fact]
    public void Seeds_en_into_translations_json_and_the_model_file_holds_no_text()
    {
        SyncTwice();

        En(TitleKey).Should().Be("Explicit text.");
        En(NotesKey).Should().Be("From the summary.");
        Translation(PlainKey).Should().BeNull();

        var model = File.ReadAllText(ModelFile("MSD_Widget"));
        model.Should().NotContain("\"description\"").And.NotContain("\"label\"");
    }

    [Fact]
    public void A_seed_keeps_the_languages_the_app_file_already_has()
    {
        WriteTranslations("""
            {
              "model": {
                "MSD_Widget": {
                  "attributes": {
                    "Title": {
                      "description": {
                        "nl": "Nederlands."
                      }
                    }
                  }
                }
              }
            }
            """);

        SyncTwice();

        Translation(TitleKey)!["nl"]!.GetValue<string>().Should().Be("Nederlands.");
        En(TitleKey).Should().Be("Explicit text.");
    }

    [Fact]
    public void Csharp_never_replaces_an_en_that_is_already_written()
    {
        // The C# summary is a seed, not an overwrite (#348 revised). A `///` comment is written for
        // the next developer; a description is an [i] tooltip for the end user. Once somebody has
        // written the user-facing text, C# stops having an opinion.
        WriteTranslations("""
            {
              "model.MSD_Widget.attributes.Title.description": {
                "en": "Hand-written help."
              }
            }
            """);

        SyncTwice();

        En(TitleKey).Should().Be("Hand-written help.");
        // Found dotted, so not written a second time nested.
        File.ReadAllText(TranslationsFile).Should().NotContain("\"Title\"");
    }

    [Fact]
    public void A_blank_en_counts_as_missing_and_is_seeded()
    {
        WriteTranslations("""
            {
              "model.MSD_Widget.attributes.Title.description": {
                "en": "   ",
                "nl": "Nederlands."
              }
            }
            """);

        SyncTwice();

        En(TitleKey).Should().Be("Explicit text.");
        Translation(TitleKey)!["nl"]!.GetValue<string>().Should().Be("Nederlands.");
    }

    [Fact]
    public void An_explicit_description_key_in_the_model_file_receives_the_seed()
    {
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":"help.widgetTitle"}
            """);

        SyncTwice();

        En("help.widgetTitle").Should().Be("Explicit text.");
        Translation(TitleKey).Should().BeNull();
        Attribute("Title").Description!.Key.Should().Be("help.widgetTitle");
    }

    [Fact]
    public void A_seed_that_would_put_a_child_under_a_translation_is_skipped()
    {
        const string original = """
            {
              "model.MSD_Widget.attributes.Title": {
                "en": "A leaf"
              }
            }
            """;
        WriteTranslations(original);

        CreateSynchronizer().SynchronizeModels(typeof(MSD_Context));

        Translation("model.MSD_Widget.attributes.Title")!["en"]!.GetValue<string>().Should().Be("A leaf");
        En(TitleKey).Should().BeNull();
    }

    // ── AC5 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Second_sync_pass_is_byte_identical_and_keeps_crlf_line_endings()
    {
        WriteTranslations("{\r\n  \"common\": {\r\n    \"hello\": {\r\n      \"en\": \"Hello\"\r\n    }\r\n  }\r\n}\r\n");

        var (firstModel, secondModel) = SyncTwice();
        var afterSecond = File.ReadAllText(TranslationsFile);
        CreateSynchronizer().SynchronizeModels(typeof(MSD_Context));

        secondModel.Should().Be(firstModel);
        File.ReadAllText(TranslationsFile).Should().Be(afterSecond);
        afterSecond.Should().Contain("\r\n").And.NotMatchRegex("[^\r]\n");

        // Since #264 the sync also points "$schema" at the published revision — but that revision comes
        // from the nearest schemas/v* git tag, and is 0 (no $schema written) where there is none. Master's
        // CI built before the deploy created schemas/v1 and passed; every build after it stamps the line,
        // and this assertion, which assumed none, then failed with no code change at all. Expect exactly
        // what this build's revision writes, so the test no longer depends on which tags the clone holds.
        var schemaLine = SparkSchemaRevision.Current > 0
            ? $"  \"$schema\": \"{SparkSchemaReference.HostedUrlFor("translations", SparkSchemaRevision.Current)}\",\r\n"
            : "";
        afterSecond.Should().StartWith("{\r\n" + schemaLine + "  \"common\": {\r\n    \"hello\"");
        afterSecond.Should().EndWith("}\r\n");
    }

    // ── AC7 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drift_report_names_only_what_sync_would_write_and_is_empty_afterwards()
    {
        // Verify and synchronize must agree (#467 D5): drift is exactly what synchronize would
        // write. A description that diverges from the C# summary is the user-facing wording.
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String"},
            {"id":"33333333-3333-3333-3333-333333333333","name":"Notes","dataType":"String"},
            {"id":"44444444-4444-4444-4444-444444444444","name":"Plain","dataType":"String"}
            """);
        WriteTranslations("""
            {
              "model.MSD_Widget.attributes.Title.description": { "en": "Hand-written help." }
            }
            """);

        var before = ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir);

        // Title has an en and is NOT reported; Plain has no C# text. Only Notes is drift.
        before.Should().BeEquivalentTo(
        [
            $"{NotesKey}: no layer of translations.json defines 'en', C# says \"From the summary.\"",
        ]);

        CreateSynchronizer().SynchronizeModels(typeof(MSD_Context));

        ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir).Should().BeEmpty();
        En(TitleKey).Should().Be("Hand-written help.");
    }

    [Fact]
    public void Drift_report_is_empty_without_a_model_directory()
    {
        ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir).Should().BeEmpty();
    }

    // ── AC12 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_logs_once_per_assembly_without_rows_and_returns_nothing()
    {
        var lines = new List<string>();
        var catalog = new AttributeDescriptionCatalog(lines.Add);
        var major = typeof(Version).GetProperty(nameof(Version.Major))!;
        var minor = typeof(Version).GetProperty(nameof(Version.Minor))!;

        catalog.Seed(major).Should().BeNull();
        catalog.Seed(minor).Should().BeNull();

        lines.Should().ContainSingle().Which.Should().Contain("Release");
    }

    [Fact]
    public void Catalog_prefers_the_explicit_attribute_and_falls_back_to_the_row()
    {
        var catalog = new AttributeDescriptionCatalog(_ => { });

        catalog.Seed(typeof(MSD_Widget).GetProperty(nameof(MSD_Widget.Title))!).Should().Be("Explicit text.");
        catalog.Seed(typeof(MSD_Widget).GetProperty(nameof(MSD_Widget.Notes))!).Should().Be("From the summary.");
        catalog.Seed(typeof(MSD_Widget).GetProperty(nameof(MSD_Widget.Plain))!).Should().BeNull();
    }
}

public class MSD_Widget
{
    public string? Id { get; set; }

    [Description("Explicit text.")]
    public string Title { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public string Plain { get; set; } = string.Empty;
}

public class MSD_Context : SparkContext
{
    public IRavenQueryable<MSD_Widget> Widgets => Session.Query<MSD_Widget>();
}
