using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Mirrors SecurityConfigurationLoaderTests for the custom-actions counterpart. The two
/// loaders are near-identical singletons that read JSON config from
/// <c>App_Data/customActions.json</c> with file-watcher hot-reload, in-memory caching,
/// and graceful empty-file fallback. A regression here makes custom actions either
/// silently unavailable or stale across hot reloads.
/// </summary>
public sealed class CustomActionsConfigurationLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly ILogger<CustomActionsConfigurationLoader> _logger = NullLogger<CustomActionsConfigurationLoader>.Instance;

    public CustomActionsConfigurationLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-customactions-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // The loader hardcodes the file at "App_Data/customActions.json", so we mirror that layout.
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* watcher locks — best-effort */ }
    }

    private CustomActionsConfigurationLoader CreateLoader() => new(_hostEnv, _logger);

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, "App_Data", "customActions.json"), json);

    private const string ValidJson = """
        {
          "ExportToExcel": {
            "displayName": { "en": "Export to Excel" },
            "icon": "download"
          },
          "ApproveLines": {
            "displayName": { "en": "Approve" }
          }
        }
        """;

    [Fact]
    public void GetConfiguration_returns_empty_when_file_does_not_exist()
    {
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Should().NotBeNull();
        config.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguration_parses_valid_json_into_action_dictionary()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Should().HaveCount(2);
        config.Should().ContainKey("ExportToExcel");
        config["ExportToExcel"].Icon.Should().Be("download");
        config["ApproveLines"].DisplayName.GetDefaultValue().Should().Be("Approve");
    }

    [Fact]
    public void GetConfiguration_throws_on_malformed_json()
    {
        WriteConfig("{ not valid json");
        using var loader = CreateLoader();

        var act = () => loader.GetConfiguration();

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void GetConfiguration_caches_result_across_calls()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();

        // Mutate the file — the cache should keep returning the original object.
        WriteConfig("{}");

        var second = loader.GetConfiguration();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void InvalidateCache_forces_next_call_to_reload_from_disk()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();
        first.Should().HaveCount(2);

        WriteConfig("{}");
        loader.InvalidateCache();

        var second = loader.GetConfiguration();

        second.Should().NotBeSameAs(first);
        second.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguration_returns_empty_when_file_content_is_literal_null()
    {
        WriteConfig("null");
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguration_is_case_insensitive_for_property_names()
    {
        WriteConfig("""
            {
              "Foo": {
                "DisplayName": { "en": "Foo Action" }
              }
            }
            """);
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Should().ContainKey("Foo");
        config["Foo"].DisplayName.GetDefaultValue().Should().Be("Foo Action");
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        WriteConfig(ValidJson);
        var loader = CreateLoader();
        _ = loader.GetConfiguration();

        loader.Dispose();
        var act = loader.Dispose;

        act.Should().NotThrow();
    }
}
