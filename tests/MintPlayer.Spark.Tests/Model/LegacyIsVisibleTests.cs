using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// #264, G-Q8: <c>isVisible</c> was removed. A model file that still says <c>"isVisible": false</c> must stop
/// the process with the replacement in the message — ignoring it (System.Text.Json's default for an unknown
/// property) would show the attribute and, where nothing else protected it, make it writable. The default,
/// <c>"isVisible": true</c>, is harmless: it loads, and <c>--spark-synchronize-model</c> drops it.
/// </summary>
/// <remarks>
/// Red before the change: the loader deserialized <c>isVisible</c> into a property and never threw, and the
/// synchronizer wrote <c>"isVisible": true</c> back into every attribute it created or kept.
/// </remarks>
public sealed class LegacyIsVisibleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _modelPath;
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog _indexCatalog = Substitute.For<IIndexCatalog>();

    public LegacyIsVisibleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-legacy-isvisible-" + Guid.NewGuid().ToString("N"));
        _modelPath = Path.Combine(_tempDir, "App_Data", "Model");
        Directory.CreateDirectory(_modelPath);
        _hostEnv.ContentRootPath.Returns(_tempDir);
        _indexCatalog.GetAllEntries().Returns([]);
        _indexCatalog.GetDefaultForCollectionType(Arg.Any<Type>()).Returns((IndexCatalogEntry?)null);
        _indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteModel(string isVisibleLiteral) => File.WriteAllText(Path.Combine(_modelPath, "LivPerson.json"), $$"""
        {
          "persistentObject": {
            "id": "{{Guid.NewGuid()}}",
            "name": "LivPerson",
            "clrType": "{{typeof(LivPerson).FullName}}",
            "attributes": [
              { "id": "{{Guid.NewGuid()}}", "name": "Name", "dataType": "string" },
              { "id": "{{Guid.NewGuid()}}", "name": "Nickname", "dataType": "string", "isVisible": {{isVisibleLiteral}} }
            ]
          },
          "queries": []
        }
        """);

    [Fact]
    public void The_loader_refuses_isVisible_false_naming_the_attribute_and_the_replacement()
    {
        WriteModel("false");

        var act = () => new ModelLoader(_hostEnv).GetEntityTypes().ToList();

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("'LivPerson.Nickname'");
        message.Should().Contain("\"showedOn\": \"None\"");
        message.Should().Contain("\"isReadOnly\": true");
        message.Should().Contain("security.json");
        message.Should().NotContain("'LivPerson.Name'", "only the hidden attribute is named");
    }

    [Fact]
    public void The_loader_accepts_isVisible_true()
    {
        WriteModel("true");

        var types = new ModelLoader(_hostEnv).GetEntityTypes().ToList();

        types.Should().ContainSingle(t => t.Name == "LivPerson")
            .Which.Attributes.Select(a => a.Name).Should().Contain("Nickname");
    }

    [Fact]
    public void Synchronize_strips_isVisible_true()
    {
        new ModelSynchronizer(_hostEnv, _indexCatalog).SynchronizeModels(typeof(LivContext));
        SetIsVisible(true);

        new ModelSynchronizer(_hostEnv, _indexCatalog).SynchronizeModels(typeof(LivContext));

        File.ReadAllText(ModelFile).Should().NotContain("isVisible");
    }

    [Fact]
    public void Synchronize_refuses_isVisible_false_and_leaves_the_file_alone()
    {
        new ModelSynchronizer(_hostEnv, _indexCatalog).SynchronizeModels(typeof(LivContext));
        SetIsVisible(false);
        var before = File.ReadAllText(ModelFile);

        var act = () => new ModelSynchronizer(_hostEnv, _indexCatalog).SynchronizeModels(typeof(LivContext));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("'LivPerson.Nickname'");
        File.ReadAllText(ModelFile).Should().Be(before, "stripping false would silently show the attribute");
    }

    private string ModelFile => Path.Combine(_modelPath, "LivPerson.json");

    private void SetIsVisible(bool value)
    {
        var json = JsonNode.Parse(File.ReadAllText(ModelFile))!;
        var nickname = json["persistentObject"]!["attributes"]!.AsArray()
            .Single(a => a!["name"]!.GetValue<string>() == "Nickname")!;
        nickname["isVisible"] = value;
        File.WriteAllText(ModelFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

public class LivPerson
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Nickname { get; set; }
}

public class LivContext : SparkContext
{
    public IRavenQueryable<LivPerson> People => Session.Query<LivPerson>();
}
