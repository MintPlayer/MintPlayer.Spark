using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Schemas;

/// <summary>
/// The synchronizer keeps <c>"$schema"</c> pointing at the package's schema revision (#264, G-Q17)
/// with a line-level edit: everything else in a hand-written file survives byte-for-byte.
/// </summary>
public sealed class SparkSchemaReferenceTests
{
    private const string Hosted = "https://schemas.spark.mintplayer.com/";

    // Comments, CRLF, tabs, a trailing space, keys in no particular order, and a "$schema" that is not
    // the root's own (inside a string, and one level down).
    private const string HandWritten =
        "{\r\n" +
        "\t\"_comment\": [ \"who may see what\", \"\\\"$schema\\\": not this one\" ],\r\n" +
        "\t\"rights\":   [ { \"resource\": \"Read/Car\",  \"groupId\": \"00000000-0000-0000-0000-000000000001\" } ], \r\n" +
        "\t\"groups\": { \"00000000-0000-0000-0000-000000000001\": \"Everyone\", \"nested\": { \"$schema\": \"" + Hosted + "v1/x.schema.json\" } }\r\n" +
        "}\r\n";

    [Fact]
    public void A_missing_schema_is_added_as_one_line_and_nothing_else_moves()
    {
        var updated = SparkSchemaReference.Apply(HandWritten, "security", 7);

        const string line = "\t\"$schema\": \"" + Hosted + "v7/security.schema.json\",\r\n";
        updated.Should().Be(HandWritten.Insert("{\r\n".Length, line));
        SparkSchemaReference.Read(updated).Should().Be(Hosted + "v7/security.schema.json");
    }

    [Fact]
    public void A_hosted_url_has_only_its_revision_rewritten()
    {
        var v3 = SparkSchemaReference.Apply(HandWritten, "security", 3);

        var v12 = SparkSchemaReference.Apply(v3, "security", 12);

        v12.Should().Be(v3.Replace(Hosted + "v3/security.schema.json", Hosted + "v12/security.schema.json"));
        // The nested hosted URL is not the root's $schema and keeps its revision.
        v12.Should().Contain(Hosted + "v1/x.schema.json");
    }

    [Fact]
    public void The_current_revision_and_any_other_schema_are_left_alone()
    {
        var current = SparkSchemaReference.Apply(HandWritten, "security", 4);
        var relative = HandWritten.Replace("{\r\n", "{\r\n\t\"$schema\": \"../../../../schemas/security.schema.json\",\r\n");
        var elsewhere = HandWritten.Replace("{\r\n", "{\r\n\t\"$schema\": \"https://example.com/v3/security.schema.json\",\r\n");

        SparkSchemaReference.Apply(current, "security", 4).Should().BeSameAs(current);
        SparkSchemaReference.Apply(relative, "security", 4).Should().BeSameAs(relative);
        SparkSchemaReference.Apply(elsewhere, "security", 4).Should().BeSameAs(elsewhere);
    }

    [Fact]
    public void Revision_0_writes_nothing()
        => SparkSchemaReference.Apply(HandWritten, "security", 0).Should().BeSameAs(HandWritten);

    [Fact]
    public void A_one_line_object_and_an_empty_one_get_the_schema_too()
    {
        SparkSchemaReference.Apply("""{ "a": 1 }""", "actions", 2)
            .Should().Be("""{ "$schema": "https://schemas.spark.mintplayer.com/v2/actions.schema.json", "a": 1 }""");
        SparkSchemaReference.Apply("{}\n", "actions", 2)
            .Should().Be("{\n  \"$schema\": \"https://schemas.spark.mintplayer.com/v2/actions.schema.json\"\n}\n");
    }

    [Fact]
    public void A_regenerated_model_file_keeps_the_schema_the_file_had()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\n  \"$schema\": \"../../../../../schemas/model.schema.json\",\n  \"persistentObject\": {}\n}\n");

            var generated = SparkSchemaReference.CarryOver(path, "{\n  \"persistentObject\": {\n    \"name\": \"Car\"\n  }\n}");

            generated.Should().Be("{\n  \"$schema\": \"../../../../../schemas/model.schema.json\",\n  \"persistentObject\": {\n    \"name\": \"Car\"\n  }\n}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyAll_covers_the_six_kinds_keeps_a_BOM_and_leaves_up_to_date_files_untouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "spark-schema-ref-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "App_Data");
        Directory.CreateDirectory(Path.Combine(appData, "Model"));
        try
        {
            File.WriteAllText(Path.Combine(appData, "Model", "Car.json"), "{\n  \"persistentObject\": {}\n}\n");
            File.WriteAllBytes(Path.Combine(appData, "security.json"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\n  \"rights\": []\n}\n")]);
            File.WriteAllText(Path.Combine(appData, "programUnits.json"), "{\n  \"programUnitGroups\": []\n}\n");
            File.WriteAllText(Path.Combine(appData, "translations.json"), "{\n  \"app\": { \"title\": { \"en\": \"T\" } }\n}\n");
            File.WriteAllText(Path.Combine(appData, "culture.json"), "{\n  \"languages\": [\"en\"]\n}\n");
            File.WriteAllText(Path.Combine(appData, "actions.json"), $"{{\n  \"$schema\": \"{Hosted}v5/actions.schema.json\"\n}}\n");
            File.WriteAllText(Path.Combine(appData, "modelHashes.json"), "{}\n");

            var changed = SparkSchemaReference.ApplyAll(root, 5);

            changed.Select(Path.GetFileName).Should().BeEquivalentTo(["Car.json", "security.json", "programUnits.json", "translations.json", "culture.json"]);
            File.ReadAllText(Path.Combine(appData, "Model", "Car.json")).Should().Contain($"\"$schema\": \"{Hosted}v5/model.schema.json\"");
            File.ReadAllText(Path.Combine(appData, "culture.json")).Should().Contain($"{Hosted}v5/culture.schema.json");
            File.ReadAllBytes(Path.Combine(appData, "security.json")).Take(3).Should().Equal([0xEF, 0xBB, 0xBF]);
            File.ReadAllText(Path.Combine(appData, "modelHashes.json")).Should().Be("{}\n");
            SparkSchemaReference.ApplyAll(root, 5).Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void The_six_kinds_are_the_six_generated_schemas()
        => SparkSchemaReference.Files.Select(f => f.Schema + ".schema.json")
            .Should().BeEquivalentTo(MintPlayer.Spark.SchemaGenerator.SparkSchemaGenerator.Files.Select(f => f.FileName));

    [Fact]
    public void Schema_changes_neither_the_model_hash_nor_what_the_loaders_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "spark-schema-hash-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "App_Data");
        Directory.CreateDirectory(Path.Combine(appData, "Model"));
        try
        {
            const string model = """
                {
                  "persistentObject": {
                    "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                    "name": "Car",
                    "attributes": [ { "id": "6f9619ff-8b86-d011-b42d-00cf4fc964fe", "name": "Brand", "showedOn": "Query" } ]
                  },
                  "queries": []
                }
                """;
            const string units = """{ "programUnitGroups": [ { "id": "6f9619ff-8b86-d011-b42d-00cf4fc964fd", "name": "programUnits.groups.main", "programUnits": [] } ] }""";
            const string actions = """{ "CarCopy": { "showedOn": "both", "selectionRule": "=1" } }""";
            const string security = """{ "groups": { "6f9619ff-8b86-d011-b42d-00cf4fc964fc": "Fleet" }, "rights": [] }""";

            var modelPath = Path.Combine(appData, "Model", "Car.json");
            Snapshot Take(string m, string u, string a, string s)
            {
                File.WriteAllText(modelPath, m);
                File.WriteAllText(Path.Combine(appData, "programUnits.json"), u);
                File.WriteAllText(Path.Combine(appData, "actions.json"), a);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return new Snapshot(
                    ModelFileShape.Describe(modelPath),
                    string.Join(";", ConfigFileShape.ComputeFileHashes(appData).Select(e => $"{e.Key}={e.Value}")),
                    JsonSerializer.Serialize(JsonSerializer.Deserialize<EntityTypeFile>(m, options)),
                    JsonSerializer.Serialize(JsonSerializer.Deserialize<ProgramUnitsConfiguration>(u, options)),
                    JsonSerializer.Serialize(SparkSecurityFiles.Compose(s, libraries: []).Configuration),
                    string.Join(";", ActionsCatalogueLoader.Build(a, SparkActionLayers.Libraries).Actions.Select(x => $"{x.Name}:{x.ShowedOn}:{x.SelectionRule}")));
            }

            var without = Take(model, units, actions, security);
            var with = Take(
                SparkSchemaReference.Apply(model, "model", 9),
                SparkSchemaReference.Apply(units, "programUnits", 9),
                // An action file may carry comments at both levels, too.
                SparkSchemaReference.Apply("""{ "_comment": "copies", "CarCopy": { "_why": "x", "showedOn": "both", "selectionRule": "=1" } }""", "actions", 9),
                SparkSchemaReference.Apply(security, "security", 9));

            with.Should().Be(without);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed record Snapshot(string ModelShape, string ConfigHashes, string Model, string Units, string Security, string Actions);
}
