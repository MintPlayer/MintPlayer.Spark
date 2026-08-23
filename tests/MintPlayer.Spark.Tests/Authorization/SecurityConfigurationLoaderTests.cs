using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

public sealed class SecurityConfigurationLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _securityFilePath = SecurityConfigurationLoader.FilePath;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly ILogger<SecurityConfigurationLoader> _logger = NullLogger<SecurityConfigurationLoader>.Instance;

    public SecurityConfigurationLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-sec-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));

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

    private SecurityConfigurationLoader CreateLoader() => new(_hostEnv, _logger);

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

    private static readonly Guid AdminsId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// The behaviour this replaces logged a warning and returned an empty configuration, which
    /// reads at runtime as a working deny-all policy. A deployment that forgot the file and an
    /// application that means to grant nothing were indistinguishable.
    /// </summary>
    [Fact]
    public void GetConfiguration_throws_when_the_file_does_not_exist()
    {
        using var loader = CreateLoader();

        var act = () => loader.GetConfiguration();

        act.Should().Throw<SparkSecurityConfigurationException>()
            .WithMessage("*--spark-init-security*", "the message must name the way out");
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

        act.Should().Throw<SparkSecurityConfigurationException>();
    }

    [Fact]
    public void GetConfiguration_caches_the_parsed_configuration()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();

        // Mutate the file on disk — cached instance should be returned on second call
        WriteConfig("""{ "groups": {}, "rights": [] }""");

        var second = loader.GetConfiguration();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void InvalidateCache_forces_next_call_to_reload()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();

        WriteConfig("""{ "groups": {}, "rights": [] }""");

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
    public void GetConfiguration_reads_App_Data_relative_to_ContentRootPath()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        loader.GetConfiguration().Rights.Should().ContainSingle();
    }

    [Fact]
    public async Task File_change_triggers_cache_invalidation()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        var first = loader.GetConfiguration();
        first.Rights.Should().ContainSingle();

        WriteConfig("""{ "groups": {}, "rights": [] }""");

        // Watcher debounces by 100ms; wait generously for the invalidation task to run.
        await AsyncWait.UntilAsync(
            () => loader.GetConfiguration().Rights.Count == 0,
            "the file watcher to invalidate the cached security configuration",
            TimeSpan.FromSeconds(3));
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

    // --- the expanded index ------------------------------------------------

    [Fact]
    public void GetResolvedRights_expands_the_configuration()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        loader.GetResolvedRights(new HashSet<Guid> { AdminsId }).Allows("Read/Person").Should().BeTrue();
    }

    [Fact]
    public void GetResolvedRights_refuses_a_caller_in_no_group()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        loader.GetResolvedRights(new HashSet<Guid>()).Allows("Read/Person").Should().BeFalse();
    }

    /// <summary>
    /// The index is keyed by the configuration instance it was derived from, so a reload cannot
    /// leave a request evaluating one file's rights against another file's expansion.
    /// </summary>
    [Fact]
    public void GetResolvedRights_follows_a_reload()
    {
        WriteConfig(ValidJson);
        using var loader = CreateLoader();

        loader.GetResolvedRights(new HashSet<Guid> { AdminsId }).Allows("Read/Person").Should().BeTrue();

        WriteConfig("""{ "groups": {}, "rights": [] }""");
        loader.InvalidateCache();

        loader.GetResolvedRights(new HashSet<Guid> { AdminsId }).Allows("Read/Person").Should().BeFalse();
    }
}
