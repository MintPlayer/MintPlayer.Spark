using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// ProgramUnitsLoader reads <c>App_Data/programUnits.json</c>, canonicalizes each unit's
/// <c>type</c> casing and validates that the field the type requires is present. A missing file
/// stays fail-soft (no menu is a valid choice); a file that exists but cannot be trusted —
/// malformed JSON, an unknown type, a missing target field — throws
/// <see cref="SparkProgramUnitsConfigurationException"/>, because the silent alternative is a
/// menu that drops entries and reads exactly like an authorization problem. Result is cached.
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

    private ProgramUnitsLoader CreateLoader() => new(_hostEnv, TranslationsLoader.For(_hostEnv, []), NullLogger<ProgramUnitsLoader>.Instance);

    /// <summary>
    /// The groups this test's own file declares. The libraries this process loads ship menu layers of their own
    /// (the identity provider's group), which every loader composes underneath the application's file.
    /// </summary>
    private static List<ProgramUnitGroup> Own(ProgramUnitsConfiguration config)
        => [.. config.ProgramUnitGroups.Where(g => !LibraryGroupIds.Contains(g.Id))];

    private static readonly HashSet<Guid> LibraryGroupIds =
        [.. (System.Text.Json.JsonSerializer.Deserialize<ProgramUnitsConfiguration>(MintPlayer.Spark.Abstractions.SparkProgramUnitsFiles.Compose(null) ?? "{}",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.ProgramUnitGroups ?? []).Select(g => g.Id)];

    private void WriteUnits(string json) =>
        File.WriteAllText(Path.Combine(_tempDir, "App_Data", "programUnits.json"), json);

    private static string UnitJson(string typeAndTarget) => $$"""
        {
          "programUnitGroups": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "programUnits.groups.fleet",
              "icon": "car",
              "order": 1,
              "programUnits": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "name": "programUnits.cars",
                  {{typeAndTarget}},
                  "order": 1
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Returns_empty_configuration_when_file_does_not_exist()
    {
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        config.Should().NotBeNull();
        Own(config).Should().BeEmpty();
    }

    [Fact]
    public void Parses_a_valid_file_and_canonicalizes_type_casing()
    {
        // "Query" (wrong case) is tolerated HERE and only here: the loader is the single place
        // that normalizes, so the endpoint and the client pipe can compare exact strings.
        WriteUnits(UnitJson("""
            "type": "Query",
            "queryId": "33333333-3333-3333-3333-333333333333"
            """));
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        var group = Own(config).Should().ContainSingle().Which;
        group.Icon.Should().Be("car");
        group.ProgramUnits.Should().ContainSingle().Which.Type.Should().Be("query");
    }

    /// <summary>
    /// #467 D1: a menu entry's name is a translations.json key. Untranslated (the loader composes
    /// no layers here), its last segment is shown humanized.
    /// </summary>
    [Fact]
    public void A_name_is_a_translation_key_shown_humanized_when_untranslated()
    {
        WriteUnits(UnitJson("""
            "type": "query",
            "queryId": "33333333-3333-3333-3333-333333333333"
            """));

        var group = Own(CreateLoader().GetProgramUnits()).Single();

        group.Name.GetValue("en").Should().Be("Fleet");
        group.ProgramUnits.Single().Name.GetValue("en").Should().Be("Cars");
    }

    [Fact]
    public void Throws_when_a_name_embeds_translated_text()
    {
        WriteUnits("""
            { "programUnitGroups": [{ "id": "11111111-1111-1111-1111-111111111111",
              "name": { "en": "Fleet" }, "order": 1, "programUnits": [] }] }
            """);

        var act = () => CreateLoader().GetProgramUnits();

        act.Should().Throw<SparkProgramUnitsConfigurationException>().WithMessage("*translations.json*");
    }

    [Fact]
    public void Parses_objectId_and_url_fields()
    {
        WriteUnits(UnitJson("""
            "type": "persistentObject",
            "persistentObjectId": "44444444-4444-4444-4444-444444444444",
            "objectId": "start"
            """));

        var unit = Own(CreateLoader().GetProgramUnits())[0].ProgramUnits[0];

        unit.Type.Should().Be("persistentObject");
        unit.ObjectId.Should().Be("start");
    }

    [Fact]
    public void Throws_on_malformed_json()
    {
        WriteUnits("{ not valid");
        var loader = CreateLoader();

        var act = () => loader.GetProgramUnits();

        act.Should().Throw<SparkProgramUnitsConfigurationException>()
            .WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Throws_on_unknown_unit_type()
    {
        WriteUnits(UnitJson("""
            "type": "dashboard"
            """));
        var loader = CreateLoader();

        var act = () => loader.GetProgramUnits();

        act.Should().Throw<SparkProgramUnitsConfigurationException>()
            .WithMessage("*unknown type 'dashboard'*");
    }

    [Theory]
    [InlineData(""" "type": "query" """, "queryId")]
    [InlineData(""" "type": "persistentObject" """, "persistentObjectId")]
    [InlineData(""" "type": "url" """, "url")]
    public void Throws_when_the_field_the_type_requires_is_missing(string type, string missingField)
    {
        WriteUnits(UnitJson(type.Trim()));
        var loader = CreateLoader();

        var act = () => loader.GetProgramUnits();

        act.Should().Throw<SparkProgramUnitsConfigurationException>()
            .WithMessage($"*no '{missingField}'*");
    }

    [Fact]
    public void Returns_empty_configuration_when_json_deserializes_to_null()
    {
        WriteUnits("null");
        var loader = CreateLoader();

        var config = loader.GetProgramUnits();

        Own(config).Should().BeEmpty();
    }

    [Fact]
    public void Result_is_cached_so_disk_changes_after_first_call_are_not_seen()
    {
        WriteUnits("""{ "programUnitGroups": [] }""");
        var loader = CreateLoader();

        var first = loader.GetProgramUnits();

        WriteUnits("""
            { "programUnitGroups": [{ "id": "00000000-0000-0000-0000-000000000001",
              "name": "programUnits.x", "order": 1 }] }
            """);

        var second = loader.GetProgramUnits();

        second.Should().BeSameAs(first);
    }
}
