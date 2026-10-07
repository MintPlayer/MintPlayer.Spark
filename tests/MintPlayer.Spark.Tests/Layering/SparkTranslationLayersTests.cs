using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// Composition M5 (D3, D10): translations compose at run time through the shared engine and
/// <see cref="SparkKinds.Translations"/>. The goldens were captured from the compiled
/// <c>SparkTranslationsRegistry</c> the host aggregator generated for QnA and CodeCoverage, before it was
/// removed (as S3 did): the run time must produce exactly what the applications ran with.
/// </summary>
public class SparkTranslationLayersTests
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static SparkTranslationsLayer Lib(string name, string json, params string[] dependsOn) => new(name, json, IsLibrary: true, dependsOn);

    private static SparkTranslationsLayer App(string json) => new("App_Data/translations.json", json, IsLibrary: false);

    private static SparkTranslationsComposition Compose(params SparkTranslationsLayer[] layers) => SparkTranslations.Compose(layers);

    private static string Languages(SparkTranslationsComposition composition, string key)
        => string.Join(", ", composition.All[key].Translations.Select(t => $"{t.Key}={t.Value}"));

    /// <summary>The libraries QnA and CodeCoverage both reference that ship translations, in layer order.</summary>
    private static SparkTranslationsLayer[] Libraries() =>
    [
        Lib("MintPlayer.Spark", File.ReadAllText(RepoFile("libs", "spark", "MintPlayer.Spark", "App_Data", "translations.json"))),
        Lib("MintPlayer.Spark.Authorization", File.ReadAllText(RepoFile("libs", "authorization", "MintPlayer.Spark.Authorization", "App_Data", "translations.json")), "MintPlayer.Spark"),
    ];

    [Theory]
    [InlineData("QnA", "QnA", "qna-translations.golden.txt")]
    [InlineData("CodeCoverage", "CodeCoverage", "codecoverage-translations.golden.txt")]
    public void An_application_composes_as_its_compiled_registry_did(string folder, string project, string golden)
    {
        var app = App(File.ReadAllText(RepoFile("apps", folder, project, "App_Data", "translations.json")));

        var composition = Compose([.. Libraries(), app]);

        Describe(composition).Should().Be(SparkLayersGoldenTests.Golden(golden));
        composition.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void The_compiled_core_layer_is_the_repository_file()
    {
        // The golden reads the libraries' files; the run time reads the attributes they compile into.
        SparkTranslations.Discover([typeof(MintPlayer.Spark.Services.ActionsCatalogueLoader).Assembly])
            .Should().ContainSingle()
            .Which.Json.Should().Be(Libraries()[0].Json);
    }

    [Fact]
    public void A_nested_namespace_and_a_dotted_key_are_the_same_key()
    {
        var composition = Compose(
            Lib("Lib", """{ "actions": { "save": { "en": "Save", "nl": "Opslaan" } } }"""),
            App("""{ "actions.save": { "nl": "Bewaren" } }"""));

        Languages(composition, "actions.save").Should().Be("en=Save, nl=Bewaren");
    }

    [Fact]
    public void A_later_layer_replaces_only_the_languages_it_states_and_appends_a_new_one()
    {
        var composition = Compose(
            Lib("Lib", """{ "save": { "en": "Save", "fr": "Enregistrer", "nl": "Opslaan" } }"""),
            App("""{ "save": { "es": "Guardar", "nl": "Bewaren" } }"""));

        // Appended, never inserted: GetValue falls back to the first language.
        Languages(composition, "save").Should().Be("en=Save, fr=Enregistrer, nl=Bewaren, es=Guardar");
        composition.All["save"].GetValue("de").Should().Be("Save");
    }

    [Fact]
    public void The_apps_empty_string_is_untranslated_and_a_librarys_is_kept()
    {
        var composition = Compose(
            Lib("Lib", """{ "save": { "en": "Save", "nl": "Opslaan" }, "blank": { "en": "" } }"""),
            App("""{ "save": { "nl": "", "es": "" }, "draft": { "en": "" } }"""));

        Languages(composition, "save").Should().Be("en=Save, nl=Opslaan");
        Languages(composition, "blank").Should().Be("en=");
        composition.All.Should().NotContainKey("draft");
    }

    [Fact]
    public void A_null_namespace_removes_every_key_below_it_and_only_those()
    {
        var composition = Compose(
            Lib("Lib", """{ "moderation": { "title": { "en": "Moderation" }, "queue": { "label": { "en": "Queue" } } }, "moderationX": { "en": "Kept" }, "save": { "en": "Save" } }"""),
            App("""{ "moderation": null }"""));

        composition.All.Keys.Should().BeEquivalentTo(["moderationX", "save"]);
    }

    [Fact]
    public void A_nested_null_removes_one_key_and_a_layer_may_restate_part_of_what_it_removes()
    {
        var composition = Compose(
            Lib("Lib", """{ "app": { "title": { "en": "Title", "nl": "Titel" }, "subtitle": { "en": "Sub" } } }"""),
            App("""{ "app": { "subtitle": null }, "app.title": null, "app.title.short": { "en": "T" } }"""));

        composition.All.Keys.Should().BeEquivalentTo(["app.title.short"]);
    }

    [Fact]
    public void A_key_removed_and_stated_again_by_a_later_layer_starts_over()
    {
        var composition = Compose(
            Lib("A.Lib", """{ "save": { "en": "Save", "nl": "Opslaan" } }"""),
            Lib("B.Lib", """{ "save": null }"""),
            App("""{ "save": { "nl": "Bewaren" } }"""));

        Languages(composition, "save").Should().Be("nl=Bewaren");
        composition.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Keys_and_languages_are_ordinal()
    {
        var composition = Compose(
            Lib("Lib", """{ "Save": { "en": "Save" } }"""),
            App("""{ "save": { "EN": "store" } }"""));

        composition.All.Keys.Should().BeEquivalentTo(["Save", "save"]);
        Languages(composition, "save").Should().Be("EN=store");
    }

    [Fact]
    public void Two_unrelated_libraries_disagreeing_is_a_conflict_and_the_later_wins()
    {
        var composition = Compose(
            Lib("A.Lib", """{ "save": { "en": "Save", "nl": "Opslaan" } }"""),
            Lib("B.Lib", """{ "save": { "en": "Store", "fr": "Enregistrer" } }"""),
            App("""{ "save": { "en": "Mine" } }"""));

        composition.Conflicts.Should().ContainSingle().Which.Should().Be(new SparkTranslationsConflict("save", "en", "B.Lib", "A.Lib"));
        Languages(composition, "save").Should().Be("en=Mine, nl=Opslaan, fr=Enregistrer");
    }

    [Fact]
    public void A_library_overriding_one_it_depends_on_is_no_conflict()
    {
        var composition = Compose(
            Lib("Z.Lib", """{ "save": { "en": "Save" } }"""),
            Lib("A.Ext", """{ "save": { "en": "Store" } }""", "Z.Lib"));

        composition.Conflicts.Should().BeEmpty();
        Languages(composition, "save").Should().Be("en=Store");
    }

    [Fact]
    public void Annotations_are_not_translations()
    {
        var composition = Compose(App("""{ "$schema": "x", "_c": "comment", "app": { "_c": "c", "title": { "_c": "c", "en": "T" } } }"""));

        composition.All.Keys.Should().BeEquivalentTo(["app.title"]);
        Languages(composition, "app.title").Should().Be("en=T");
    }

    [Theory]
    [InlineData("""{ "a": { "en": "x", "b": { "en": "y" } } }""", "mixes texts with namespaces")]
    [InlineData("""{ "a": { "en": 1 } }""", "mixes texts with namespaces")]
    [InlineData("""{ "a": { "en": ["x"] } }""", "is an array")]
    [InlineData("""{ "a.b": { "en": "x" }, "a": { "b": { "en": "y" } } }""", "'a.b' is stated twice")]
    [InlineData("""{ "a": { "en": "x" }, }""", "not valid JSON")]
    [InlineData("""[ "a" ]""", "must be a JSON object")]
    public void A_layer_that_cannot_be_read_is_refused_naming_it(string json, string reason)
    {
        var act = () => Compose(Lib("Lib", """{ "save": { "en": "Save" } }"""), App(json));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("App_Data/translations.json").And.Contain(reason);
    }

    [Fact]
    public void An_empty_namespace_states_nothing()
    {
        var composition = Compose(Lib("Lib", """{ "save": { "en": "Save" } }"""), App("""{ "app": {} }"""));

        composition.All.Keys.Should().BeEquivalentTo(["save"]);
    }

    /// <summary>The golden format: one line per key, ordinal; its languages in order, each text as relaxed JSON.</summary>
    private static string Describe(SparkTranslationsComposition composition)
    {
        var builder = new StringBuilder();
        foreach (var (key, text) in composition.All.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(key);
            foreach (var (language, value) in text.Translations)
                builder.Append('\t').Append(language).Append('=').Append(JsonSerializer.Serialize(value, Relaxed));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
            directory = directory.Parent;

        return directory is null
            ? throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.")
            : Path.Combine([directory.FullName, .. parts]);
    }
}
