using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Composition M5 (D10): the running translations are the library layers composed with the
/// application's <c>App_Data/translations.json</c>, read from disk. The snapshot is replaced whole when the
/// file changes; a file that no longer composes leaves the previous snapshot in place. This is what
/// <c>GET /spark/translations</c> serves.
/// </summary>
public sealed class TranslationsLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _appFile;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();

    private static readonly SparkTranslationsLayer Library =
        new("Lib", """{ "save": { "en": "Save", "nl": "Opslaan" }, "cancel": { "en": "Cancel" } }""", IsLibrary: true);

    public TranslationsLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-translations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        _appFile = Path.Combine(_tempDir, "App_Data", "translations.json");
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private TranslationsLoader Loader() => TranslationsLoader.For(_hostEnv, [Library]);

    [Fact]
    public void The_library_layers_compose_with_the_application_file_on_disk()
    {
        File.WriteAllText(_appFile, """{ "save": { "nl": "Bewaren" }, "app": { "title": { "en": "App" } } }""");
        using var loader = Loader();

        loader.Resolve("save")!.Translations.Select(t => $"{t.Key}={t.Value}").Should().Equal("en=Save", "nl=Bewaren");
        loader.Resolve("app.title")!.GetValue("en").Should().Be("App");
        loader.Resolve("cancel")!.GetValue("en").Should().Be("Cancel");
        loader.Resolve("__definitely_not_a_real_translation_key__").Should().BeNull();
    }

    [Fact]
    public void Without_an_application_file_the_library_layers_are_the_translations()
    {
        using var loader = Loader();

        loader.GetAll().Keys.Should().BeEquivalentTo(["save", "cancel"]);
    }

    [Fact]
    public void Every_key_in_GetAll_resolves()
    {
        File.WriteAllText(_appFile, """{ "app": { "title": { "en": "App" } } }""");
        using var loader = Loader();

        foreach (var key in loader.GetAll().Keys)
            loader.Resolve(key).Should().NotBeNull($"key '{key}' is in GetAll() so Resolve must find it");
    }

    [Fact]
    public void A_file_that_does_not_compose_stops_the_first_read()
    {
        File.WriteAllText(_appFile, """{ "save": { "en": "x", "child": { "en": "y" } } }""");
        using var loader = Loader();

        var act = () => loader.GetAll();

        act.Should().Throw<InvalidOperationException>().WithMessage("*translations.json*mixes texts with namespaces*");
    }

    [Fact]
    public void A_reload_swaps_in_a_new_snapshot_and_leaves_the_old_one_untouched()
    {
        File.WriteAllText(_appFile, """{ "save": { "nl": "Bewaren" } }""");
        using var loader = Loader();
        var before = loader.GetAll();

        File.WriteAllText(_appFile, """{ "save": { "nl": "Opslaan!" }, "cancel": null }""");
        loader.Reload().Should().BeTrue();

        var after = loader.GetAll();
        after.Should().NotBeSameAs(before);
        after["save"].GetValue("nl").Should().Be("Opslaan!");
        after.Should().NotContainKey("cancel");
        before["save"].GetValue("nl").Should().Be("Bewaren", "a reader holding the old snapshot never sees a half-built one");
        before.Should().ContainKey("cancel");
    }

    [Fact]
    public void A_reload_of_a_file_that_does_not_compose_keeps_the_previous_snapshot()
    {
        File.WriteAllText(_appFile, """{ "save": { "nl": "Bewaren" } }""");
        using var loader = Loader();
        var before = loader.GetAll();

        File.WriteAllText(_appFile, """{ "save": { "nl": "Bewa""");
        loader.Reload().Should().BeFalse();

        loader.GetAll().Should().BeSameAs(before);
    }

    [Fact]
    public async Task Saving_the_application_file_reloads_the_translations()
    {
        File.WriteAllText(_appFile, """{ "save": { "nl": "Bewaren" } }""");
        using var loader = Loader();
        loader.GetAll()["save"].GetValue("nl").Should().Be("Bewaren");

        File.WriteAllText(_appFile, """{ "save": { "nl": "Vastleggen" } }""");

        // A failure bound, not a wait: the watcher debounces 100 ms.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (loader.GetAll()["save"].GetValue("nl") != "Vastleggen" && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        loader.GetAll()["save"].GetValue("nl").Should().Be("Vastleggen");
    }
}
