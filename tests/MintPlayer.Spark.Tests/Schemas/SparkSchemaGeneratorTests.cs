using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.SchemaGenerator;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Schemas;

/// <summary>
/// Guards for the generated JSON schemas (#264 M6, G-Q11..Q13, G-Q21): the output is deterministic
/// (the publish workflow mints a revision whenever it differs from the latest release), every
/// App_Data file in this repository validates against the schema generated from this commit, and
/// each schema is exported from what its loader actually reads.
/// </summary>
public sealed class SparkSchemaGeneratorTests
{
    [Fact]
    public void Generation_is_deterministic()
    {
        var first = SparkSchemaGenerator.Generate();
        var second = SparkSchemaGenerator.Generate();

        second.Keys.Should().BeEquivalentTo(first.Keys);
        foreach (var (fileName, content) in first)
        {
            second[fileName].Should().Be(content, $"{fileName} must be byte-for-byte stable");
            content.Should().NotContain("\r", $"{fileName} must use \\n line endings on every OS");
            content.Should().EndWith("}\n");
        }
    }

    [Fact]
    public void Generates_the_six_schemas()
        => SparkSchemaGenerator.Generate().Keys.Should().BeEquivalentTo(
        [
            "model.schema.json", "security.schema.json", "programUnits.schema.json",
            "translations.schema.json", "culture.schema.json", "actions.schema.json",
        ]);

    [Fact]
    public void Every_App_Data_file_in_the_repository_validates()
    {
        var schemas = Schemas();
        var failures = new List<string>();
        var validated = 0;

        foreach (var (path, schemaName) in RepositoryFiles())
        {
            validated++;
            var problems = Validate(schemas[schemaName], File.ReadAllText(path));
            if (problems.Count > 0)
                failures.Add($"{Path.GetRelativePath(RepositoryRoot(), path)}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", problems)}");
        }

        // A glob that matched nothing would pass vacuously.
        validated.Should().BeGreaterThan(40);
        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void A_property_the_server_does_not_read_is_rejected_and_an_underscore_comment_is_not()
    {
        var model = Schemas()["model"];
        const string valid = """
            {
              "$schema": "../../../../../schemas/model.schema.json",
              "_comment": ["anything", 1, true],
              "persistentObject": {
                "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                "name": "Car",
                "_note": "a comment at any depth",
                "attributes": [
                  { "id": "6f9619ff-8b86-d011-b42d-00cf4fc964fe", "name": "Brand", "showedOn": "Query, PersistentObject" }
                ]
              }
            }
            """;

        Validate(model, valid).Should().BeEmpty();
        Validate(model, valid.Replace("\"showedOn\"", "\"isVisible\": false, \"showedOn\"")).Should().NotBeEmpty();
        Validate(model, valid.Replace("\"Query, PersistentObject\"", "\"Query, Detail\"")).Should().NotBeEmpty();
        Validate(model, valid.Replace("\"_comment\"", "\"comment\"")).Should().NotBeEmpty();
    }

    [Fact]
    public void A_translation_tree_mixing_leaves_and_namespaces_is_rejected()
    {
        var translations = Schemas()["translations"];

        Validate(translations, """{ "$schema": "x", "_c": "c", "app": { "title": { "_c": "c", "en": "T", "nl": "T" } } }""").Should().BeEmpty();
        Validate(translations, """{ "app": { "title": { "en": "T" }, "en": "T" } }""").Should().NotBeEmpty();
        // The generators' parser (MiniJson) refuses numbers and arrays in translations.json, comments included.
        Validate(translations, """{ "_c": 1, "app": { "title": { "en": "T" } } }""").Should().NotBeEmpty();
    }

    [Fact]
    public void The_schemas_are_exported_from_the_types_the_loaders_deserialize()
    {
        var types = SparkSchemaGenerator.Files.ToDictionary(f => f.Name, f => f.Type);

        // ModelLoader, SecurityConfigurationLoader and ProgramUnitsLoader deserialize these types.
        types["model"].Should().Be(typeof(EntityTypeFile));
        types["security"].Should().Be(typeof(SecurityConfiguration));
        types["programUnits"].Should().Be(typeof(ProgramUnitsConfiguration));
        // A tree read by the translations source generator; no CLR type describes it.
        types["translations"].Should().BeNull();
    }

    [Fact]
    public void The_actions_entry_mirrors_the_properties_the_catalogue_binds()
    {
        var mirrored = typeof(ActionsFileEntry).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

        mirrored.Should().BeEquivalentTo(ActionsCatalogueLoader.KnownProperties);
    }

    [Fact]
    public void The_culture_file_mirrors_what_CultureLoader_reads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spark-schema-culture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "App_Data"));
        try
        {
            var file = new CultureFile { Languages = ["en", "nl"], DefaultLanguage = "nl" };
            File.WriteAllText(Path.Combine(directory, "App_Data", "culture.json"),
                JsonSerializer.Serialize(file, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var host = Substitute.For<IHostEnvironment>();
            host.ContentRootPath.Returns(directory);

            var culture = new CultureLoader(host).GetCulture();

            // Every mirrored property reached the loader: neither fell back to its default.
            culture.Languages.Keys.Should().BeEquivalentTo(["en", "nl"]);
            culture.DefaultLanguage.Should().Be("nl");
            typeof(CultureFile).GetProperties().Should().HaveCount(2);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>Every hand-edited App_Data file of the six kinds in this repository, with its schema's name.</summary>
    internal static IEnumerable<(string Path, string Schema)> RepositoryFiles()
    {
        var root = RepositoryRoot();
        var appData = Directory.GetDirectories(Path.Combine(root, "apps"))
            .SelectMany(Directory.GetDirectories)
            .Concat(Directory.GetDirectories(Path.Combine(root, "libs")).SelectMany(Directory.GetDirectories))
            .Select(project => Path.Combine(project, "App_Data"))
            .Where(Directory.Exists)
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var directory in appData)
        {
            var model = Path.Combine(directory, "Model");
            if (Directory.Exists(model))
                foreach (var file in Directory.GetFiles(model, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                    yield return (file, "model");

            foreach (var name in new[] { "security", "programUnits", "translations", "culture", "actions" })
            {
                var file = Path.Combine(directory, name + ".json");
                if (File.Exists(file))
                    yield return (file, name);
            }
        }
    }

    private static Dictionary<string, JsonSchema> Schemas()
    {
        // A registry per call: the schemas carry no $id, so each gets its own base URI.
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        return SparkSchemaGenerator.Files.ToDictionary(
            f => f.Name,
            f => JsonSchema.FromText(SparkSchemaGenerator.Generate()[f.FileName], options, new Uri($"https://schemas.test/{f.FileName}")));
    }

    private static List<string> Validate(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
            return [];

        return (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}"))
            .DefaultIfEmpty("invalid")
            .ToList();
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
