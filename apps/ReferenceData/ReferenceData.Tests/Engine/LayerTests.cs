using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Client;

namespace ReferenceData.Tests.Engine;

/// <summary>
/// Plan R10: the read-only local copies (countries, places, postal codes) are declared in the client's library layer,
/// and an instance that is also a consumer (apps/ReferenceData) composes each of them once: the instance package
/// references the client and no longer ships those types itself, so the "same type in two layers" collision (SPARK057)
/// cannot arise.
/// </summary>
public class LayerTests
{
    private static readonly string[] LocalCopies = ["RefCountry", "RefPlace", "RefPostalCode"];

    private static IReadOnlyList<SparkLibrary> Libraries()
        => SparkLayerCatalog.Discover([
            typeof(RefPlace).Assembly,                 // abstractions: no layer
            typeof(ReleaseSyncService).Assembly,       // the client: reference-data-client
            typeof(SparkReferenceDataExtensions).Assembly, // the instance: reference-data
        ]);

    private static IEnumerable<(SparkLibrary Library, JsonObject Root)> ModelFiles()
        => Libraries().SelectMany(l => l.Layers.Where(x => x.Kind == "model").Select(x => (l, JsonNode.Parse(x.Json)!.AsObject())));

    private static string? NameOf(JsonObject root) => root["persistentObject"]?["name"]?.GetValue<string>();

    [Fact]
    public void The_local_copy_types_are_declared_once_in_the_client_layer()
    {
        foreach (var type in LocalCopies)
        {
            var declarers = ModelFiles().Where(f => NameOf(f.Root) == type).Select(f => f.Library.Alias).ToList();
            declarers.Should().ContainSingle();
            declarers[0].Should().Be("reference-data-client");
        }
    }

    [Fact]
    public void The_instance_layer_keeps_only_its_own_types()
    {
        var instance = ModelFiles().Where(f => f.Library.Alias == "reference-data").Select(f => NameOf(f.Root)).ToList();
        foreach (var type in LocalCopies) instance.Should().NotContain(type);
        instance.Should().Contain("RefStreet");
    }

    [Fact]
    public void The_local_copies_are_read_only()
    {
        foreach (var (_, root) in ModelFiles().Where(f => LocalCopies.Contains(NameOf(f.Root))))
            foreach (var attribute in root["persistentObject"]!["attributes"]!.AsArray())
                attribute!["isReadOnly"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void No_library_grants_write_rights_on_reference_types()
    {
        foreach (var library in Libraries())
            foreach (var layer in library.Layers.Where(x => x.Kind == "security"))
                foreach (var right in JsonNode.Parse(layer.Json)!["rights"]?.AsArray() ?? [])
                {
                    var resource = right!["resource"]!.GetValue<string>();
                    resource.Should().NotContain("Edit");
                    resource.Should().NotContain("New");
                    resource.Should().NotContain("Delete");
                    // The instance may not grant on types the client declares (library guard rail).
                    if (library.Alias == "reference-data")
                        LocalCopies.Should().NotContain(resource[(resource.IndexOf('/') + 1)..]);
                }
    }

    [Fact]
    public void The_client_layer_is_stacked_below_the_instance()
        => Libraries().Single(l => l.Alias == "reference-data").DependsOn.Should().Contain(typeof(ReleaseSyncService).Assembly.GetName().Name!);
}
