using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// M5 left the labels the model, the action catalogue, the program units and the culture resolve at
/// load behind a translations reload. Through the one watcher policy (composition D8) they follow it:
/// when the translations' reload returns, every derived label has moved too.
/// </summary>
public sealed class LabelsFollowTranslationsReloadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly TranslationsLoader _translations;

    public LabelsFollowTranslationsReloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-labels-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data", "Model"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
        _translations = TranslationsLoader.For(_hostEnv, []);
    }

    public void Dispose()
    {
        _translations.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* watcher locks — best-effort */ }
    }

    private void WriteAppData(string relative, string json)
        => File.WriteAllText(Path.Combine(_tempDir, "App_Data", relative), json);

    /// <summary>Translates <paramref name="key"/> to <paramref name="english"/> and reloads, as the watcher would after a save.</summary>
    private void Translate(string key, string english, bool reload)
    {
        WriteAppData("translations.json", $$"""{ "{{key}}": { "en": "{{english}}" } }""");
        if (reload)
            _translations.Reload().Should().BeTrue();
    }

    [Fact]
    public void The_model_labels_follow()
    {
        WriteAppData(Path.Combine("Model", "Car.json"), """
            { "persistentObject": { "id": "11111111-1111-1111-1111-111111111111", "name": "Car", "clrType": "Demo.Car", "attributes": [] }, "queries": [] }
            """);
        Translate("model.Car.label", "Car", reload: false);
        using var loader = new ModelLoader(ModelSource.For(_hostEnv, []), _translations, NullLogger<ModelLoader>.Instance);
        loader.GetEntityTypeByName("Car")!.Label!.GetValue("en").Should().Be("Car");

        Translate("model.Car.label", "Automobile", reload: true);

        loader.GetEntityTypeByName("Car")!.Label!.GetValue("en").Should().Be("Automobile");
    }

    [Fact]
    public void The_action_catalogue_labels_follow()
    {
        WriteAppData("actions.json", """{ "Archive": {} }""");
        Translate("actions.Archive.label", "Archive", reload: false);
        using var loader = new ActionsCatalogueLoader(_hostEnv, NullLogger<ActionsCatalogueLoader>.Instance, _translations);
        loader.GetCatalogue().Find("Archive")!.Label.GetValue("en").Should().Be("Archive");

        Translate("actions.Archive.label", "Put away", reload: true);

        loader.GetCatalogue().Find("Archive")!.Label.GetValue("en").Should().Be("Put away");
    }

    [Fact]
    public void The_program_unit_names_follow()
    {
        WriteAppData("programUnits.json", """
            { "programUnitGroups": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "programUnits.groups.fleet", "order": 1, "programUnits": [] } ] }
            """);
        Translate("programUnits.groups.fleet", "Fleet", reload: false);
        using var loader = new ProgramUnitsLoader(_hostEnv, _translations, NullLogger<ProgramUnitsLoader>.Instance);
        loader.GetProgramUnits().ProgramUnitGroups.Single(g => g.Id == Guid.Parse("11111111-1111-1111-1111-111111111111")).Name.GetValue("en").Should().Be("Fleet");

        Translate("programUnits.groups.fleet", "Vehicles", reload: true);

        loader.GetProgramUnits().ProgramUnitGroups.Single(g => g.Id == Guid.Parse("11111111-1111-1111-1111-111111111111")).Name.GetValue("en").Should().Be("Vehicles");
    }

    [Fact]
    public void The_culture_language_names_follow()
    {
        WriteAppData("culture.json", """{ "languages": ["en", "nl"], "defaultLanguage": "en" }""");
        Translate("culture.languages.nl", "Dutch", reload: false);
        using var loader = new CultureLoader(_hostEnv, _translations, NullLogger<CultureLoader>.Instance);
        loader.GetCulture().Languages["nl"].GetValue("en").Should().Be("Dutch");

        Translate("culture.languages.nl", "Nederlands", reload: true);

        loader.GetCulture().Languages["nl"].GetValue("en").Should().Be("Nederlands");
    }

    [Fact]
    public void A_translations_reload_that_does_not_compose_leaves_the_labels_alone()
    {
        WriteAppData("actions.json", """{ "Archive": {} }""");
        Translate("actions.Archive.label", "Archive", reload: false);
        using var loader = new ActionsCatalogueLoader(_hostEnv, NullLogger<ActionsCatalogueLoader>.Instance, _translations);
        var before = loader.GetCatalogue();

        WriteAppData("translations.json", "{ not valid");
        _translations.Reload().Should().BeFalse();

        loader.GetCatalogue().Should().BeSameAs(before, "no reload happened, so nothing derived from it moves");
    }
}
