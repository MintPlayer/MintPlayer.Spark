using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// The synchronizer writes only the application's delta for a type a library ships (composition D6):
/// never the library's id, never what the library already states, and no file at all when nothing
/// differs. The library layer is built from the synchronizer's own output, so the comparison is exact.
/// </summary>
public class SynchronizeLibraryDeltaTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-libdelta-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment hostEnvironment = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog indexCatalog = Substitute.For<IIndexCatalog>();

    public SynchronizeLibraryDeltaTests()
    {
        Directory.CreateDirectory(contentRoot);
        hostEnvironment.ContentRootPath.Returns(contentRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelFile => Path.Combine(contentRoot, "App_Data", "Model", nameof(LibGadget) + ".json");

    private void Synchronize(IReadOnlyList<SparkLibrary> libraries)
        => new ModelSynchronizer(hostEnvironment, indexCatalog) { Libraries = libraries }.SynchronizeModels(typeof(LibGadgetContext));

    /// <summary>What the synchronizer writes for <see cref="LibGadget"/> when the application owns it, shipped as a library's layer.</summary>
    private SparkLibrary LibraryFromOwnOutput(Func<JsonObject, JsonObject>? edit = null)
    {
        // Twice: the first run materialises defaults; the second is the fixed point (SynchronizeIdempotencyTests).
        Synchronize([]);
        Synchronize([]);
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(ModelFile))!;
        File.Delete(ModelFile);
        root = edit?.Invoke(root) ?? root;
        return new SparkLibrary("gadgets", "Gadgets.Lib", [], [new SparkLibraryLayer("model", "Model/LibGadget.json", root.ToJsonString())]);
    }

    [Fact]
    public void Nothing_differing_writes_no_file()
    {
        var library = LibraryFromOwnOutput();

        Synchronize([library]);

        File.Exists(ModelFile).Should().BeFalse("the type is the library's and the application changes nothing");
    }

    [Fact]
    public void Only_what_the_library_lacks_is_written_and_never_its_id()
    {
        var library = LibraryFromOwnOutput(root =>
        {
            ((JsonArray)root["persistentObject"]!["attributes"]!).Clear();
            return root;
        });
        var libraryId = JsonNode.Parse(library.Layers[0].Json)!["persistentObject"]!["id"]!.GetValue<string>();

        Synchronize([library]);

        File.Exists(ModelFile).Should().BeTrue();
        var delta = (JsonObject)JsonNode.Parse(File.ReadAllText(ModelFile))!;
        var po = (JsonObject)delta["persistentObject"]!;
        po.ContainsKey("id").Should().BeFalse("a library's id is fixed and never repeated in the application");
        po["name"]!.GetValue<string>().Should().Be(nameof(LibGadget));
        po["attributes"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).Should().Equal(nameof(LibGadget.Name));
        delta.ContainsKey("queries").Should().BeFalse("the library already has the query");
        File.ReadAllText(ModelFile).Should().NotContain(libraryId);
    }

    private static JsonObject WithColumnOrder(JsonObject root, params string[] columnOrder)
    {
        var query = (JsonObject)root["queries"]!.AsArray().Single()!;
        query["columnOrder"] = new JsonArray([.. columnOrder.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        return root;
    }

    [Fact]
    public void A_library_owned_column_order_is_left_to_the_library()
    {
        // #284 D16: "Gone" names no attribute and Name is unlisted, so an application list would change
        // twice. Maintaining the library's list would copy it whole into the application file and freeze it.
        var library = LibraryFromOwnOutput(root => WithColumnOrder(root, "Gone"));

        Synchronize([library]);

        File.Exists(ModelFile).Should().BeFalse("the list is the library's, unchanged by the application");
    }

    [Fact]
    public void An_application_column_order_on_a_library_query_is_maintained_as_a_delta()
    {
        // #284 S4: the application's list on the library's query is the application's to maintain, and the
        // delta carries only the query's name and the list, never a copy of the query.
        var library = LibraryFromOwnOutput();
        var queryName = JsonNode.Parse(library.Layers[0].Json)!["queries"]!.AsArray().Single()!["name"]!.GetValue<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(ModelFile)!);
        File.WriteAllText(ModelFile, new JsonObject
        {
            ["persistentObject"] = new JsonObject { ["name"] = nameof(LibGadget) },
            ["queries"] = new JsonArray(new JsonObject { ["name"] = queryName, ["columnOrder"] = new JsonArray("Gone") }),
        }.ToJsonString());

        Synchronize([library]);
        var first = File.ReadAllText(ModelFile);
        Synchronize([library]);

        File.ReadAllText(ModelFile).Should().Be(first, "the maintained delta is a fixed point");
        var query = (JsonObject)JsonNode.Parse(first)!["queries"]!.AsArray().Single()!;
        query.Select(m => m.Key).Should().BeEquivalentTo(["name", "columnOrder"]);
        query["columnOrder"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal(nameof(LibGadget.Name));
    }

    [Fact]
    public void The_written_delta_is_a_fixed_point()
    {
        var library = LibraryFromOwnOutput(root =>
        {
            ((JsonArray)root["persistentObject"]!["attributes"]!).Clear();
            return root;
        });

        Synchronize([library]);
        var first = File.ReadAllText(ModelFile);
        Synchronize([library]);

        File.ReadAllText(ModelFile).Should().Be(first);
    }

    // ── D28: identity, not name (F7) ───────────────────────────────────────────────────────────

    [Fact]
    public void The_delta_is_anchored_on_the_library_type_with_extends()
    {
        var library = LibraryFromOwnOutput(root =>
        {
            ((JsonArray)root["persistentObject"]!["attributes"]!).Clear();
            return root;
        });

        Synchronize([library]);

        var delta = (JsonObject)JsonNode.Parse(File.ReadAllText(ModelFile))!;
        delta["$extends"]!.GetValue<string>().Should().Be("gadgets:LibGadget");
        delta.Select(m => m.Key).First(k => k != "$schema").Should().Be("$extends", "the anchor is the first thing a reader sees");
    }

    [Fact]
    public void An_application_class_named_like_a_library_type_of_another_class_is_refused_before_anything_is_written()
    {
        // F7 (red before the fix): the synchronizer found the library type by NAME and wrote a delta on it, so the
        // application's LibGadget took the library's id and rights while the library's row policies (keyed by CLR
        // type) stopped applying to it.
        var library = LibraryFromOwnOutput(root =>
        {
            root["persistentObject"]!["clrType"] = "Other.Library.LibGadget";
            return root;
        });

        var act = () => Synchronize([library]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*LibGadget*gadgets:LibGadget*PersistentObjectName<*LibGadget>*");
        File.Exists(ModelFile).Should().BeFalse("the refusal comes before any write");
    }

    [Fact]
    public void A_delta_written_before_extends_is_anchored_by_the_synchronizer()
    {
        var library = LibraryFromOwnOutput(root =>
        {
            ((JsonArray)root["persistentObject"]!["attributes"]!).Clear();
            return root;
        });
        Directory.CreateDirectory(Path.GetDirectoryName(ModelFile)!);
        File.WriteAllText(ModelFile, """{ "persistentObject": { "name": "LibGadget", "attributes": [ { "name": "Name", "dataType": "string" } ] } }""");

        Synchronize([library]);

        JsonNode.Parse(File.ReadAllText(ModelFile))!["$extends"]!.GetValue<string>().Should().Be("gadgets:LibGadget");
    }

    [Fact]
    public void Renaming_an_application_type_moves_its_file_queries_translations_and_rights_and_keeps_its_id()
    {
        Synchronize([]);
        var before = (JsonObject)JsonNode.Parse(File.ReadAllText(ModelFile))!;
        var id = before["persistentObject"]!["id"]!.GetValue<string>();
        File.WriteAllText(Path.Combine(contentRoot, "App_Data", "translations.json"),
            """{ "model": { "LibGadget": { "label": { "en": "Gadget" } } } }""");
        File.WriteAllText(Path.Combine(contentRoot, "App_Data", "security.json"),
            """{ "rights": [ { "key": "r1", "resource": "QueryRead/LibGadget", "groupId": "00000000-0000-0000-0000-000000000001" }, { "key": "r2", "resource": "Edit/LibGadget/Name", "groupId": "00000000-0000-0000-0000-000000000001" } ] }""");

        new ModelSynchronizer(hostEnvironment, indexCatalog)
        {
            Libraries = [],
            Renames = [new PersistentObjectNameAttribute<LibGadget>("Gizmo")],
        }.SynchronizeModels(typeof(LibGadgetContext));

        var moved = Path.Combine(contentRoot, "App_Data", "Model", "Gizmo.json");
        File.Exists(ModelFile).Should().BeFalse("the file moves with the name");
        var after = (JsonObject)JsonNode.Parse(File.ReadAllText(moved))!;
        after["persistentObject"]!["name"]!.GetValue<string>().Should().Be("Gizmo");
        after["persistentObject"]!["id"]!.GetValue<string>().Should().Be(id, "a rename keeps the id");
        after["queries"]!.AsArray().Select(q => q!["entityType"]!.GetValue<string>()).Should().OnlyContain(t => t == "Gizmo");
        var translations = File.ReadAllText(Path.Combine(contentRoot, "App_Data", "translations.json"));
        translations.Should().Contain("\"Gizmo\"").And.NotContain("\"LibGadget\"");
        var security = File.ReadAllText(Path.Combine(contentRoot, "App_Data", "security.json"));
        security.Should().Contain("QueryRead/Gizmo").And.Contain("Edit/Gizmo/Name").And.NotContain("LibGadget");
    }

    [Fact]
    public void Rights_on_an_old_name_another_type_now_has_are_listed_and_never_rewritten()
    {
        var path = Path.Combine(contentRoot, "security.json");
        const string Security = """{ "rights": [ { "key": "r1", "resource": "QueryRead/Order", "groupId": "00000000-0000-0000-0000-000000000001" } ] }""";
        File.WriteAllText(path, Security);

        var moved = ModelRenameMigration.RetargetRights(
            path,
            new Dictionary<string, string> { ["Order"] = "WebshopOrder" },
            new HashSet<string>(["Order", "WebshopOrder"], StringComparer.OrdinalIgnoreCase));

        moved.Should().BeFalse("a right on 'Order' would silently cover the new type that took the name");
        File.ReadAllText(path).Should().Be(Security);
    }
}

public sealed class LibGadget
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class LibGadgetContext : SparkContext
{
    public IRavenQueryable<LibGadget> LibGadgets => Session.Query<LibGadget>();
}
