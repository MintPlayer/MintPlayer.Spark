using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// ProgramUnitsLoader is a thin file → object loader for <c>App_Data/programUnits.json</c>.
/// Same shape as CultureLoader: missing file → empty default, malformed → empty default
/// (silent fallback so a bad config doesn't crash app startup), result is cached.
/// </summary>
public sealed class ProgramUnitsLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();

    public ProgramUnitsLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-progunits-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ProgramUnitsLoader CreateLoader() => new(_hostEnv);

    private void WriteUnits(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, "App_Data", "programUnits.json"), json);

    [Fact]
    public void Returns_empty_configuration_when_file_does_not_exist()
    {
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        config.Should().NotBeNull();
        config.ProgramUnitGroups.Should().BeEmpty();
    }

    [Fact]
    public void Parses_program_unit_groups_from_a_valid_file()
    {
        WriteUnits("""
            {
              "programUnitGroups": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": { "en": "Fleet" },
                  "icon": "car",
                  "order": 1,
                  "programUnits": [
                    {
                      "id": "22222222-2222-2222-2222-222222222222",
                      "name": { "en": "Cars" },
                      "type": "Query",
                      "queryId": "33333333-3333-3333-3333-333333333333",
                      "order": 1
                    }
                  ]
                }
              ]
            }
            """);
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        config.ProgramUnitGroups.Should().ContainSingle();
        var group = config.ProgramUnitGroups[0];
        group.Icon.Should().Be("car");
        group.ProgramUnits.Should().ContainSingle().Which.Type.Should().Be("Query");
    }

    [Fact]
    public void Falls_back_to_empty_configuration_on_malformed_json()
    {
        WriteUnits("{ not valid");
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        config.ProgramUnitGroups.Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_configuration_when_json_deserializes_to_null()
    {
        WriteUnits("null");
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        config.ProgramUnitGroups.Should().BeEmpty();
    }

    [Fact]
    public void Result_is_cached_so_disk_changes_after_first_call_are_not_seen()
    {
        WriteUnits("""{ "programUnitGroups": [] }""");
        var loader = CreateLoader();

        var first = loader.GetProgramUnits();

        WriteUnits("""
            { "programUnitGroups": [{ "id": "00000000-0000-0000-0000-000000000001",
              "name": { "en": "X" }, "order": 1 }] }
            """);

        var second = loader.GetProgramUnits();

        second.Should().BeSameAs(first);
    }
}
