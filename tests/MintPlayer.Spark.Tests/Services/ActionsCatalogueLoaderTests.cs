using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The composed action catalogue (#467, D7/S12): the libraries' compiled <c>actions.json</c> layers,
/// core first, with the application's <c>App_Data/actions.json</c> on top, per property.
/// </summary>
public sealed class ActionsCatalogueLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();

    public ActionsCatalogueLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-actions-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* watcher locks — best-effort */ }
    }

    private ActionsCatalogueLoader CreateLoader() => new(_hostEnv, NullLogger<ActionsCatalogueLoader>.Instance, TranslationsLoader.For(_hostEnv, []));

    private void WriteApp(string json) => File.WriteAllText(ActionsCatalogueLoader.PathFor(_tempDir), json);

    private static SparkActionsLayer Library(string name, string json) => new(name, json, IsLibrary: true);

    // ── The core layer ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_an_app_file_the_catalogue_is_the_core_New_Edit_and_Delete()
    {
        using var loader = CreateLoader();

        var catalogue = loader.GetCatalogue();

        catalogue.Actions.Select(a => a.Name).Should().Equal("New", "Edit", "Delete");
        catalogue.Find("edit")!.SelectionRule.Should().Be("=1");
        catalogue.Find("Edit")!.ShowedOn.Should().Be("both");
        catalogue.Find("Delete")!.SelectionRule.Should().Be(">0");
        catalogue.Find("Delete")!.Variant.Should().Be("danger");
        catalogue.Find("New")!.ShowedOn.Should().Be("query");
        catalogue.Actions.Should().OnlyContain(a => a.IsBuiltIn && a.DeclaredBy == "MintPlayer.Spark");
    }

    [Fact]
    public void A_literal_null_app_file_adds_nothing()
    {
        WriteApp("null");
        using var loader = CreateLoader();

        loader.GetCatalogue().Actions.Select(a => a.Name).Should().Equal("New", "Edit", "Delete");
    }

    // ── Composition ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_app_property_overrides_the_inherited_one_and_the_rest_is_kept()
    {
        var catalogue = ActionsCatalogueLoader.Build("""{ "delete": { "selectionRule": "=1" } }""", SparkActionLayers.Libraries);

        var delete = catalogue.Find("Delete")!;
        delete.Name.Should().Be("Delete", "the name keeps the declaring layer's spelling");
        delete.SelectionRule.Should().Be("=1");
        delete.Variant.Should().Be("danger");
        delete.Sources["selectionRule"].Should().Be(SparkActionLayers.AppLayerName);
        delete.Sources["variant"].Should().Be("MintPlayer.Spark");
    }

    [Fact]
    public void Null_removes_an_inherited_action()
    {
        var catalogue = ActionsCatalogueLoader.Build("""{ "Edit": null }""", SparkActionLayers.Libraries);

        catalogue.Find("Edit").Should().BeNull();
        catalogue.Actions.Select(a => a.Name).Should().Equal("New", "Delete");
    }

    [Fact]
    public void A_property_set_to_null_resets_it_to_the_default()
    {
        var catalogue = ActionsCatalogueLoader.Build("""{ "Delete": { "selectionRule": null, "showedOn": null } }""", SparkActionLayers.Libraries);

        var delete = catalogue.Find("Delete")!;
        delete.SelectionRule.Should().BeNull();
        delete.ShowedOn.Should().Be("both");
        delete.Sources.Should().NotContainKey("selectionRule");
    }

    [Fact]
    public void A_name_no_layer_declares_adds_an_action_after_the_inherited_ones()
    {
        var catalogue = ActionsCatalogueLoader.Build("""{ "Archive": { "showedOn": "detail" } }""", SparkActionLayers.Libraries);

        catalogue.Actions.Select(a => a.Name).Should().Equal("New", "Edit", "Delete", "Archive");
        catalogue.Find("Archive")!.IsBuiltIn.Should().BeFalse();
        catalogue.Find("Archive")!.DeclaredBy.Should().Be(SparkActionLayers.AppLayerName);
    }

    [Fact]
    public void Two_libraries_stating_a_property_differently_is_a_conflict_the_later_one_wins()
    {
        var catalogue = ActionsCatalogueLoader.Build(null,
        [
            Library("A.Lib", """{ "Archive": { "icon": "box", "showedOn": "both" } }"""),
            Library("B.Lib", """{ "Archive": { "icon": "archive", "showedOn": "both" } }"""),
        ]);

        catalogue.Find("Archive")!.Icon.Should().Be("archive");
        catalogue.Conflicts.Should().ContainSingle()
            .Which.Should().Be(new SparkActionsConflict("Archive", "icon", "B.Lib", "A.Lib"));
    }

    [Fact]
    public void The_app_overriding_a_library_is_no_conflict()
    {
        var catalogue = ActionsCatalogueLoader.Build("""{ "Archive": { "icon": "app" } }""",
            [Library("A.Lib", """{ "Archive": { "icon": "box" } }""")]);

        catalogue.Find("Archive")!.Icon.Should().Be("app");
        catalogue.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Library_layers_are_discovered_core_first_in_layer_order()
    {
        SparkActionLayers.Libraries.Should().NotBeEmpty();
        SparkActionLayers.Libraries[0].Name.Should().Be("MintPlayer.Spark");
        SparkActionLayers.Libraries.Select(l => l.Name).Should().Equal(
            SparkLayerCatalog.Of("actions").Select(x => x.Library.AssemblyName));
    }

    // ── Validation ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_malformed_rule_is_reported_together_naming_the_action_and_the_rule()
    {
        var act = () => ActionsCatalogueLoader.Build("""
            { "Archive": { "selectionRule": "1-5" }, "Publish": { "selectionRule": "=abc" } }
            """, SparkActionLayers.Libraries);

        var message = act.Should().Throw<FormatException>().Which.Message;
        message.Should().Contain("Archive").And.Contain("1-5").And.Contain("Publish").And.Contain("=abc");
    }

    [Theory]
    [InlineData("=1")]
    [InlineData(">0")]
    [InlineData("<=5")]
    [InlineData("!=0")]
    [InlineData("1<X<5")]
    [InlineData("0<X")]
    [InlineData("=0")]
    public void A_well_formed_rule_loads(string rule)
    {
        var catalogue = ActionsCatalogueLoader.Build($$"""{ "Copy": { "selectionRule": "{{rule}}" } }""", SparkActionLayers.Libraries);

        catalogue.Find("Copy")!.SelectionRule.Should().Be(rule);
    }

    [Theory]
    [InlineData("displayName", "actions.Archive.label")]
    [InlineData("confirmationMessageKey", "actions.Archive.confirmation")]
    public void The_pre_467_properties_are_refused_with_the_key_to_use(string property, string key)
    {
        var act = () => ActionsCatalogueLoader.Build($$"""{ "Archive": { "{{property}}": "x" } }""", SparkActionLayers.Libraries);

        act.Should().Throw<FormatException>().WithMessage($"*{property}*{key}*");
    }

    [Fact]
    public void Embedded_text_is_refused_naming_the_key_it_belongs_under()
    {
        var act = () => ActionsCatalogueLoader.Build("""{ "Archive": { "label": { "en": "Archive" } } }""", SparkActionLayers.Libraries);

        act.Should().Throw<FormatException>().WithMessage("*actions.Archive.label*");
    }

    [Fact]
    public void An_unknown_property_and_a_bad_showedOn_are_refused()
    {
        var act = () => ActionsCatalogueLoader.Build("""{ "Archive": { "colour": "red", "showedOn": "sidebar" } }""", SparkActionLayers.Libraries);

        act.Should().Throw<FormatException>().Which.Message.Should().Contain("colour").And.Contain("sidebar");
    }

    [Fact]
    public void Malformed_json_is_refused_naming_the_layer()
    {
        var act = () => ActionsCatalogueLoader.Build("{ not valid", SparkActionLayers.Libraries);

        act.Should().Throw<FormatException>().WithMessage($"*{SparkActionLayers.AppLayerName}*");
    }

    [Fact]
    public void An_action_declared_twice_in_different_casing_is_refused()
    {
        var act = () => ActionsCatalogueLoader.Build("""{ "Archive": {}, "archive": {} }""", SparkActionLayers.Libraries);

        act.Should().Throw<FormatException>().WithMessage("*twice*");
    }

    // ── Text (D26): resolved here; this project compiles no app translations ───────────────────

    [Fact]
    public void An_untranslated_label_is_the_humanized_name_and_no_description_is_invented()
    {
        var archive = ActionsCatalogueLoader.Build("""{ "ArchiveCar": {} }""", SparkActionLayers.Libraries).Find("ArchiveCar")!;

        archive.Label.GetValue("en").Should().Be("Archive Car");
        archive.Description.Should().BeNull();
        archive.Confirmation.Should().BeNull("nothing asks for one");
    }

    // The_core_texts_come_from_the_core_translations lives in CoreActionTextsTests: it needs the
    // process-wide translations registry, so it runs in a collection of its own.

    [Fact]
    public void An_explicit_untranslated_confirmation_key_still_asks_and_false_never_does()
    {
        var catalogue = ActionsCatalogueLoader.Build("""
            { "Archive": { "confirmation": "nobody.translated.this" }, "Delete": { "confirmation": false } }
            """, SparkActionLayers.Libraries);

        catalogue.Find("Archive")!.Confirmation.Should().NotBeNull();
        catalogue.Find("Delete")!.Confirmation.Should().BeNull();
    }

    // ── The loader ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_result_is_cached_until_invalidated()
    {
        WriteApp("""{ "Archive": {} }""");
        using var loader = CreateLoader();
        var first = loader.GetCatalogue();

        WriteApp("""{ "Publish": {} }""");
        loader.GetCatalogue().Should().BeSameAs(first);

        loader.InvalidateCache();
        loader.GetCatalogue().Find("Publish").Should().NotBeNull();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var loader = CreateLoader();
        loader.GetCatalogue();

        loader.Dispose();
        var act = () => loader.Dispose();

        act.Should().NotThrow();
    }

    // ── --spark-print-effective-actions and the model hash ─────────────────────────────────────

    [Fact]
    public void The_print_switch_names_each_property_with_its_source_layer()
    {
        WriteApp("""{ "Delete": { "selectionRule": "=1" }, "Edit": null }""");

        var output = SparkDevelopmentExtensions.DescribeEffectiveActions(_tempDir, SparkActionLayers.Libraries);

        output.Should().Contain($"MintPlayer.Spark → {SparkActionLayers.AppLayerName}");
        output.Should().MatchRegex(@"selectionRule\s+= =1\s+\[App_Data/actions\.json\]");
        output.Should().MatchRegex(@"variant\s+= danger\s+\[MintPlayer\.Spark\]");
        output.Should().NotContain("Edit  (declared by");
    }

    [Fact]
    public void The_model_hash_covers_the_composed_rules_so_a_library_change_shows_up()
    {
        var appData = Path.Combine(_tempDir, "App_Data");
        var before = ConfigFileShape.ComputeFileHashes(appData, [Library("A.Lib", """{ "Archive": { "selectionRule": "=1" } }""")]);
        var after = ConfigFileShape.ComputeFileHashes(appData, [Library("A.Lib", """{ "Archive": { "selectionRule": ">0" } }""")]);
        var relabelled = ConfigFileShape.ComputeFileHashes(appData, [Library("A.Lib", """{ "Archive": { "selectionRule": "=1", "icon": "box" } }""")]);

        before[ConfigFileShape.ActionsFileName].Should().NotBe(after[ConfigFileShape.ActionsFileName]);
        before[ConfigFileShape.ActionsFileName].Should().Be(relabelled[ConfigFileShape.ActionsFileName], "an icon changes appearance, not behaviour");
    }
}
