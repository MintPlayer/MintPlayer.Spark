using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// CultureLoader reads <c>App_Data/culture.json</c> on first access and caches the result
/// for the process lifetime. Three contracts to pin: missing file → default config (en),
/// malformed file → default config (silent fallback), and the result is cached so the
/// frontend doesn't re-read disk on every request.
/// </summary>
public sealed class CultureLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();

    public CultureLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-culture-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private CultureLoader CreateLoader() => new(_hostEnv);

    private void WriteCulture(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, "App_Data", "culture.json"), json);

    [Fact]
    public void GetCulture_returns_default_configuration_when_file_does_not_exist()
    {
        var loader = CreateLoader();

        var config = loader.GetCulture();

        config.DefaultLanguage.Should().Be("en");
        config.Languages.Should().ContainKey("en");
    }

    [Fact]
    public void GetCulture_parses_languages_from_a_valid_culture_file()
    {
        WriteCulture("""
            {
              "defaultLanguage": "nl",
              "languages": {
                "en": { "en": "English" },
                "nl": { "en": "Dutch", "nl": "Nederlands" }
              }
            }
            """);
        var loader = CreateLoader();

        var config = loader.GetCulture();

        config.DefaultLanguage.Should().Be("nl");
        config.Languages.Should().ContainKeys("en", "nl");
        config.Languages["nl"].Translations["nl"].Should().Be("Nederlands");
    }

    [Fact]
    public void GetCulture_falls_back_to_default_on_malformed_json_without_throwing()
    {
        // CultureLoader catches parse failures and returns a default config — the framework
        // would prefer to come up with English-only than fail to start because of a typo.
        WriteCulture("{ not valid json");
        var loader = CreateLoader();

        var config = loader.GetCulture();

        config.DefaultLanguage.Should().Be("en");
        config.Languages.Should().ContainKey("en");
    }

    [Fact]
    public void GetCulture_returns_default_when_json_deserializes_to_null()
    {
        WriteCulture("null");
        var loader = CreateLoader();

        var config = loader.GetCulture();

        config.DefaultLanguage.Should().Be("en");
    }

    [Fact]
    public void GetCulture_is_cached_so_disk_changes_after_first_call_are_not_seen()
    {
        WriteCulture("""{ "defaultLanguage": "en", "languages": { "en": { "en": "English" } } }""");
        var loader = CreateLoader();

        var first = loader.GetCulture();

        // Mutate disk — the cached Lazy<T> keeps returning the original instance.
        WriteCulture("""{ "defaultLanguage": "fr", "languages": { "fr": { "fr": "Français" } } }""");

        var second = loader.GetCulture();

        second.Should().BeSameAs(first);
        second.DefaultLanguage.Should().Be("en");
    }
}
