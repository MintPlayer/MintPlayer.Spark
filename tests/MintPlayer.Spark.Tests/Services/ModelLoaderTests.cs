using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// ModelLoader walks <c>App_Data/Model/*.json</c> on first access and indexes
/// <c>EntityTypeDefinition</c>s by id, alias (auto-generated from Name when not explicit),
/// and aggregates the queries declared in each file. ResolveEntityType dispatches by
/// Guid-or-alias for URL routing — a regression silently 404s entity-type pages.
/// </summary>
public sealed class ModelLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();

    public ModelLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-modelloader-tests-" + Guid.NewGuid().ToString("N"));
        _hostEnv.ContentRootPath.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ModelLoader CreateLoader() => new(_hostEnv);

    private string ModelDir
    {
        get
        {
            var dir = Path.Combine(_tempDir, "App_Data", "Model");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void WriteModel(string fileName, string json) =>
        File.WriteAllText(Path.Combine(ModelDir, fileName), json);

    private static string ModelJson(
        string id,
        string name,
        string clrType,
        string? alias = null,
        string[]? queries = null)
    {
        var aliasField = alias is null ? "" : $$""", "alias": "{{alias}}" """;
        var queriesArray = queries is null
            ? "[]"
            : "[" + string.Join(",", queries.Select((qid, i) =>
                $$"""
                {
                  "id": "{{qid}}",
                  "name": "Q{{i}}",
                  "source": "Database.X"
                }
                """)) + "]";
        return $$"""
            {
              "persistentObject": {
                "id": "{{id}}",
                "name": "{{name}}",
                "clrType": "{{clrType}}"{{aliasField}}
              },
              "queries": {{queriesArray}}
            }
            """;
    }

    [Fact]
    public void Returns_empty_when_model_directory_does_not_exist()
    {
        var loader = CreateLoader();

        loader.GetEntityTypes().Should().BeEmpty();
        loader.GetQueries().Should().BeEmpty();
    }

    [Fact]
    public void Loads_entity_types_from_json_files_in_the_Model_directory()
    {
        WriteModel("Car.json", ModelJson("11111111-1111-1111-1111-111111111111", "Car", "Demo.Car"));
        WriteModel("Person.json", ModelJson("22222222-2222-2222-2222-222222222222", "Person", "Demo.Person"));
        var loader = CreateLoader();

        var types = loader.GetEntityTypes().ToList();

        types.Should().HaveCount(2);
        types.Select(t => t.Name).Should().BeEquivalentTo(["Car", "Person"]);
    }

    [Fact]
    public void Auto_generates_alias_from_Name_by_lowercasing_when_not_explicit()
    {
        WriteModel("Car.json", ModelJson("11111111-1111-1111-1111-111111111111", "Car", "Demo.Car"));
        var loader = CreateLoader();

        loader.GetEntityTypeByAlias("car").Should().NotBeNull();
    }

    [Fact]
    public void Explicit_alias_overrides_the_auto_generated_one()
    {
        WriteModel("Car.json", ModelJson(
            "11111111-1111-1111-1111-111111111111", "Car", "Demo.Car", alias: "vehicle"));
        var loader = CreateLoader();

        loader.GetEntityTypeByAlias("vehicle").Should().NotBeNull();
        loader.GetEntityTypeByAlias("car").Should().BeNull("auto alias must NOT be registered when an explicit one is set");
    }

    [Fact]
    public void Duplicate_aliases_keep_the_first_first_wins()
    {
        // Both files claim alias "x" — the first file's entity wins; the second logs a warning.
        WriteModel("A.json", ModelJson("11111111-1111-1111-1111-111111111111", "AlphaA", "Demo.A", alias: "x"));
        WriteModel("B.json", ModelJson("22222222-2222-2222-2222-222222222222", "BetaB", "Demo.B", alias: "x"));
        var loader = CreateLoader();

        var resolved = loader.GetEntityTypeByAlias("x");

        resolved.Should().NotBeNull();
        // We don't pin which file wins (depends on Directory.GetFiles ordering) — just that
        // exactly one of them is bound to the alias.
        resolved!.Name.Should().BeOneOf("AlphaA", "BetaB");
    }

    [Fact]
    public void GetEntityType_finds_by_id()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        WriteModel("Car.json", ModelJson(id.ToString(), "Car", "Demo.Car"));
        var loader = CreateLoader();

        loader.GetEntityType(id).Should().NotBeNull();
        loader.GetEntityType(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void GetEntityTypeByName_is_case_insensitive()
    {
        WriteModel("Car.json", ModelJson("11111111-1111-1111-1111-111111111111", "Car", "Demo.Car"));
        var loader = CreateLoader();

        loader.GetEntityTypeByName("CAR").Should().NotBeNull();
        loader.GetEntityTypeByName("Nope").Should().BeNull();
    }

    [Fact]
    public void GetEntityTypeByClrType_returns_match_or_null()
    {
        WriteModel("Car.json", ModelJson("11111111-1111-1111-1111-111111111111", "Car", "Demo.Car"));
        var loader = CreateLoader();

        loader.GetEntityTypeByClrType("Demo.Car").Should().NotBeNull();
        loader.GetEntityTypeByClrType("Demo.Other").Should().BeNull();
    }

    [Fact]
    public void ResolveEntityType_dispatches_Guid_string_to_id_lookup_and_others_to_alias()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        WriteModel("Car.json", ModelJson(id.ToString(), "Car", "Demo.Car"));
        var loader = CreateLoader();

        loader.ResolveEntityType(id.ToString()).Should().NotBeNull();
        loader.ResolveEntityType("car").Should().NotBeNull();
        loader.ResolveEntityType("nope").Should().BeNull();
    }

    [Fact]
    public void GetQueries_aggregates_from_all_model_files_and_auto_populates_EntityType()
    {
        WriteModel("Car.json", ModelJson(
            "11111111-1111-1111-1111-111111111111", "Car", "Demo.Car",
            queries: ["aaaaaaaa-1111-0000-0000-000000000001", "aaaaaaaa-1111-0000-0000-000000000002"]));
        WriteModel("Person.json", ModelJson(
            "22222222-2222-2222-2222-222222222222", "Person", "Demo.Person",
            queries: ["bbbbbbbb-1111-0000-0000-000000000003"]));
        var loader = CreateLoader();

        var queries = loader.GetQueries().ToList();

        queries.Should().HaveCount(3);
        queries.Where(q => q.EntityType == "Car").Should().HaveCount(2);
        queries.Where(q => q.EntityType == "Person").Should().ContainSingle();
    }

    [Fact]
    public void Malformed_model_files_are_skipped_other_files_still_load()
    {
        WriteModel("Car.json", ModelJson("11111111-1111-1111-1111-111111111111", "Car", "Demo.Car"));
        WriteModel("Broken.json", "{ not valid json");
        WriteModel("Person.json", ModelJson("22222222-2222-2222-2222-222222222222", "Person", "Demo.Person"));
        var loader = CreateLoader();

        var types = loader.GetEntityTypes().ToList();

        types.Should().HaveCount(2);
        types.Select(t => t.Name).Should().BeEquivalentTo(["Car", "Person"]);
    }

    [Fact]
    public void Result_is_cached_so_files_added_after_first_call_are_not_seen()
    {
        WriteModel("Car.json", ModelJson("11111111-1111-1111-1111-111111111111", "Car", "Demo.Car"));
        var loader = CreateLoader();

        loader.GetEntityTypes().Should().ContainSingle();

        WriteModel("Person.json", ModelJson("22222222-2222-2222-2222-222222222222", "Person", "Demo.Person"));

        loader.GetEntityTypes().Should().ContainSingle("the loader is Singleton + Lazy — disk changes after first call are not seen");
    }
}
