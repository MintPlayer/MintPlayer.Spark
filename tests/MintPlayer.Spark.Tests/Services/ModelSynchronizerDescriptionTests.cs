using System.ComponentModel;
using System.Text.Json;
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
/// #348 — how C# text becomes an attribute's <c>description</c> on synchronize, and who owns it
/// afterwards. The C# summary is a SEED: it fills <c>en</c> when the model file has nothing there,
/// and JSON owns the value from then on, in every language including <c>en</c>.
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

    // ── AC1 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hand_authored_description_survives_sync_unchanged_when_csharp_is_silent()
    {
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Plain","dataType":"String",
             "description":{"nl":"Handmatig.","en":"By hand.","fr":"À la main."}}
            """);

        var (first, second) = SyncTwice();

        var description = Attribute("Plain").Description!.Translations;
        description.Keys.Should().Equal("nl", "en", "fr");
        description.Values.Should().Equal("Handmatig.", "By hand.", "À la main.");
        second.Should().Be(first);
    }

    // ── AC2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Description_attribute_seeds_en_on_a_new_attribute()
    {
        SyncTwice();

        var description = Attribute("Title").Description!.Translations;
        description.Keys.Should().Equal("en");
        description["en"].Should().Be("Explicit text.");
    }

    [Fact]
    public void Description_attribute_seeds_en_on_an_existing_attribute_and_preserves_other_languages()
    {
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":{"nl":"Nederlands."}}
            """);

        SyncTwice();

        var description = Attribute("Title").Description!.Translations;
        description.Keys.Should().Equal("en", "nl");
        description.Values.Should().Equal("Explicit text.", "Nederlands.");
    }

    [Fact]
    public void Csharp_never_replaces_an_en_that_is_already_written()
    {
        // The C# summary is a seed, not an overwrite (#348 revised). A `///` comment
        // is written for the next developer; a description is an [i] tooltip for the
        // end user. Once somebody has written the user-facing text, C# stops having
        // an opinion — even when its own summary has since changed.
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":{"en":"Hand-written help.","nl":"Nederlands."}}
            """);

        SyncTwice();

        var description = Attribute("Title").Description!.Translations;
        description.Keys.Should().Equal("en", "nl");
        description.Values.Should().Equal("Hand-written help.", "Nederlands.");
    }

    [Fact]
    public void A_blank_en_counts_as_missing_and_is_seeded()
    {
        // "Present" means it has text. An empty or whitespace `en` is an absence
        // wearing a key, so the seed still fills it.
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":{"en":"   ","nl":"Nederlands."}}
            """);

        SyncTwice();

        var description = Attribute("Title").Description!.Translations;
        description.Keys.Should().Equal("en", "nl");
        description.Values.Should().Equal("Explicit text.", "Nederlands.");
    }

    // ── AC3 / AC4 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generated_summary_row_seeds_en()
    {
        SyncTwice();

        var description = Attribute("Notes").Description!.Translations;
        description.Keys.Should().Equal("en");
        description["en"].Should().Be("From the summary.");
    }

    [Fact]
    public void Description_attribute_wins_over_the_generated_summary()
    {
        SyncTwice();

        Attribute("Title").Description!.Translations["en"].Should().Be("Explicit text.");
    }

    [Fact]
    public void Undocumented_property_gets_no_description()
    {
        SyncTwice();

        Attribute("Plain").Description.Should().BeNull();
        File.ReadAllText(ModelFile("MSD_Widget")).Should().NotContain("\"description\": null");
    }

    // ── AC5 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Second_sync_pass_is_byte_identical_in_every_seeding_configuration()
    {
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":{"nl":"Nederlands."}},
            {"id":"33333333-3333-3333-3333-333333333333","name":"Notes","dataType":"String",
             "description":{"en":"Stale.","fr":"Français."}},
            {"id":"44444444-4444-4444-4444-444444444444","name":"Plain","dataType":"String",
             "description":{"nl":"Alleen JSON."}}
            """);

        var (first, second) = SyncTwice();

        second.Should().Be(first);
    }

    // ── AC7 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drift_report_names_only_what_sync_would_fill_and_is_empty_afterwards()
    {
        // Verify and synchronize must agree: drift is exactly what synchronize
        // would write. A description that merely diverges from the C# summary is
        // the user-facing wording and is not drift — reporting it would fail the
        // build over text synchronize will never change.
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":{"en":"Hand-written help.","nl":"Nederlands."}},
            {"id":"33333333-3333-3333-3333-333333333333","name":"Notes","dataType":"String"},
            {"id":"44444444-4444-4444-4444-444444444444","name":"Plain","dataType":"String",
             "description":{"en":"   "}}
            """);

        var before = ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir);

        // Title diverges from its C# text and is NOT reported; Plain is blank but has
        // no C# text to seed from, so there is nothing to fill. Only Notes is drift.
        before.Should().BeEquivalentTo(
        [
            "MSD_Widget.Notes: description.en is absent on disk, C# says \"From the summary.\"",
        ]);

        CreateSynchronizer().SynchronizeModels(typeof(MSD_Context));

        ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir).Should().BeEmpty();
        Attribute("Title").Description!.Translations["en"].Should().Be("Hand-written help.");
    }

    [Fact]
    public void A_blank_en_with_csharp_text_behind_it_is_drift()
    {
        SeedWidgetFile("""
            {"id":"22222222-2222-2222-2222-222222222222","name":"Title","dataType":"String",
             "description":{"en":"","nl":"Nederlands."}}
            """);

        ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir).Should().BeEquivalentTo(
        [
            "MSD_Widget.Title: description.en is blank on disk, C# says \"Explicit text.\"",
        ]);
    }

    [Fact]
    public void Drift_report_is_empty_without_a_model_directory()
    {
        ModelSynchronizer.DescribeDescriptionDrift(typeof(MSD_Context), _tempDir).Should().BeEmpty();
    }

    // ── Seeding rule in isolation ───────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyDescriptionSeed_fills_an_absent_or_blank_en_and_never_replaces_a_written_one()
    {
        var absent = new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "A" };
        absent.Description = new TranslatedString { Translations = { ["nl"] = "N", ["fr"] = "F" } };
        ModelSynchronizer.ApplyDescriptionSeed(absent, "E");
        absent.Description.Translations.Keys.Should().Equal("en", "nl", "fr");

        // Written by a human: left alone, key order untouched.
        var present = new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "B" };
        present.Description = new TranslatedString { Translations = { ["nl"] = "N", ["en"] = "old" } };
        ModelSynchronizer.ApplyDescriptionSeed(present, "new");
        present.Description.Translations.Keys.Should().Equal("nl", "en");
        present.Description.Translations["en"].Should().Be("old");

        // Blank is an absence wearing a key — filled in place, so a second pass is
        // byte-identical.
        var blank = new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "D" };
        blank.Description = new TranslatedString { Translations = { ["nl"] = "N", ["en"] = "  " } };
        ModelSynchronizer.ApplyDescriptionSeed(blank, "new");
        blank.Description.Translations.Keys.Should().Equal("nl", "en");
        blank.Description.Translations["en"].Should().Be("new");

        var untouched = new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "C" };
        ModelSynchronizer.ApplyDescriptionSeed(untouched, null);
        untouched.Description.Should().BeNull();
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
