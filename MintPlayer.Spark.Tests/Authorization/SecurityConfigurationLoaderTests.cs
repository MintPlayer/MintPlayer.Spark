using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

public sealed class SecurityConfigurationLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _securityFilePath;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly ILogger<SecurityConfigurationLoader> _logger = NullLogger<SecurityConfigurationLoader>.Instance;

    public SecurityConfigurationLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-sec-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _securityFilePath = "security.json";

        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — file watchers in tests may briefly hold locks
        }
    }

    private SecurityConfigurationLoader CreateLoader(AuthorizationOptions? opts = null)
    {
        opts ??= new AuthorizationOptions
        {
            SecurityFilePath = _securityFilePath,
            CacheRights = true,
            CacheExpirationMinutes = 5,
            EnableHotReload = false // default off for most tests
        };
        return new SecurityConfigurationLoader(Options.Create(opts), _hostEnv, _logger);
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, _securityFilePath), json);

    private const string ValidJson = """
        {
          "groups": {
            "11111111-1111-1111-1111-111111111111": { "en": "Admins" }
          },
          "rights": [
            {
              "id": "aaaa0000-0000-0000-0000-000000000001",
              "resource": "Read/Person",
              "groupId": "11111111-1111-1111-1111-111111111111",
              "isDenied": false,
              "isImportant": false
            }
          ]
        }
        """;

    [Fact]
    public void GetConfiguration_returns_empty_when_file_does_not_exist()
    {
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Should().NotBeNull();
        config.Groups.Should().BeEmpty();
        config.Rights.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguration_parses_valid_json_file()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Groups.Should().HaveCount(1);
        config.Groups.Should().ContainKey("11111111-1111-1111-1111-111111111111");
        config.Rights.Should().ContainSingle()
            .Which.Resource.Should().Be("Read/Person");
    }

    [Fact]
    public void GetConfiguration_is_case_insensitive_for_property_names()
    {
        WriteConfig("""{ "Groups": { "aaaa0000-0000-0000-0000-000000000002": { "en": "Users" } }, "Rights": [] }""");
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Groups.Should().ContainKey("aaaa0000-0000-0000-0000-000000000002");
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
    public void GetConfiguration_caches_result_when_CacheRights_enabled()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();

        // Mutate the file on disk — cached instance should be returned on second call
        File.WriteAllText(Path.Combine(_tempDir, _securityFilePath),
            """{ "groups": {}, "rights": [] }""");

        var second = loader.GetConfiguration();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void GetConfiguration_rereads_file_when_CacheRights_disabled()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader(new AuthorizationOptions
        {
            SecurityFilePath = _securityFilePath,
            CacheRights = false,
            EnableHotReload = false
        });

        var first = loader.GetConfiguration();
        var second = loader.GetConfiguration();

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void InvalidateCache_forces_next_call_to_reload()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();

        File.WriteAllText(Path.Combine(_tempDir, _securityFilePath),
            """{ "groups": {}, "rights": [] }""");

        loader.InvalidateCache();
        var second = loader.GetConfiguration();

        second.Should().NotBeSameAs(first);
        second.Rights.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguration_returns_empty_when_file_content_is_literal_null()
    {
        WriteConfig("null");
        using var loader = CreateLoader();

        var config = loader.GetConfiguration();

        config.Groups.Should().BeEmpty();
        config.Rights.Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguration_reads_directory_relative_to_ContentRootPath()
    {
        var subDir = Path.Combine(_tempDir, "App_Data");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "security.json"), ValidJson);

        using var loader = CreateLoader(new AuthorizationOptions
        {
            SecurityFilePath = "App_Data/security.json",
            CacheRights = true,
            EnableHotReload = false
        });

        var config = loader.GetConfiguration();

        config.Rights.Should().ContainSingle();
    }

    [Fact]
    public async Task File_change_triggers_cache_invalidation_when_hot_reload_enabled()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader(new AuthorizationOptions
        {
            SecurityFilePath = _securityFilePath,
            CacheRights = true,
            EnableHotReload = true
        });

        var first = loader.GetConfiguration();
        first.Rights.Should().ContainSingle();

        File.WriteAllText(Path.Combine(_tempDir, _securityFilePath),
            """{ "groups": {}, "rights": [] }""");

        // Watcher debounces by 100ms; wait generously for the invalidation task to run.
        await WaitForCondition(() =>
        {
            var current = loader.GetConfiguration();
            return current.Rights.Count == 0;
        }, TimeSpan.FromSeconds(3));

        loader.GetConfiguration().Rights.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        WriteConfig(ValidJson);
        var loader = CreateLoader();
        _ = loader.GetConfiguration();

        loader.Dispose();
        var act = () => loader.Dispose();

        act.Should().NotThrow();
    }

    private static async Task WaitForCondition(Func<bool> predicate, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }
}
