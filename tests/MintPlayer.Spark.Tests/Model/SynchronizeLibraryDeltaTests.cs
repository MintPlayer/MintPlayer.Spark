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
