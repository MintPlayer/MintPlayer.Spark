using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// The gates hash the composed result and record which layer stated what (composition D7). A library
/// update that changes a model structure, an action's rule or a right fails the gate, and the message
/// names the library (PRD §7 acceptance).
/// </summary>
public sealed class LayerProvenanceGateTests : IDisposable
{
    private readonly string _root;

    public LayerProvenanceGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "spark-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "App_Data", "Model"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private const string Gadget = "36b0f3e1-2a4c-5d9e-8f10-112233445566";

    /// <summary>A library shipping the type <c>Gadget</c>; <paramref name="serial"/> is its attribute <c>Serial</c>'s extra fields.</summary>
    private static SparkLibrary Library(string serial = "", string label = "Gadget", string? actions = null)
        => new("gadgets", "Fixture.Gadgets", [],
        [
            new SparkLibraryLayer("model", "Model/Gadget.json", $$"""
                { "persistentObject": { "id": "{{Gadget}}", "name": "Gadget", "label": "{{label}}",
                  "attributes": [ { "id": "46b0f3e1-2a4c-5d9e-8f10-112233445566", "name": "Serial", "dataType": "string"{{serial}} } ] } }
                """),
            .. actions is null ? Array.Empty<SparkLibraryLayer>() : [new SparkLibraryLayer("actions", "actions.json", actions)],
        ]);

    /// <summary>The model's part of <c>modelHashes.json</c>, computed as <c>BuildModelHashes</c> does, without a context type.</summary>
    private ModelHashFile Hashes(params SparkLibrary[] libraries)
    {
        var files = ModelFileShape.ComputeFileHashes(libraries, ModelHashFile.ModelDirectoryFor(_root));
        var config = ConfigFileShape.ComputeFileHashes(SparkAppData.Directory(_root), libraries.SelectMany(LibraryActions).ToList());
        var (names, layers) = ModelHashFile.ComputeLayers(libraries, _root);
        return new ModelHashFile
        {
            ModelHash = SparkModelShape.ComputeModelHash(
                new Dictionary<string, string>(), "", ModelHashFile.CombineFileHashes(files),
                config.Count > 0 ? ModelHashFile.CombineFileHashes(config) : null,
                ModelHashFile.CombineLayerHashes(layers)),
            Files = files,
            ConfigFiles = config,
            Libraries = names.Count > 0 ? names : null,
            Layers = layers.Count > 0 ? layers : null,
        };
    }

    private static IEnumerable<Abstractions.Actions.SparkActionsLayer> LibraryActions(SparkLibrary library)
        => library.Layers.Where(l => l.Kind == "actions").Select(l => new Abstractions.Actions.SparkActionsLayer(library.AssemblyName, l.Json, IsLibrary: true));

    private void WriteApp(string relative, string json) => File.WriteAllText(Path.Combine(_root, "App_Data", relative), json);

    [Fact]
    public void The_hash_file_records_each_library_by_alias_and_assembly_and_each_layer_of_a_composed_type()
    {
        WriteApp(Path.Combine("Model", "Gadget.json"), """{ "persistentObject": { "name": "Gadget", "attributes": [ { "name": "Serial", "showedOn": "PersistentObject" } ] } }""");

        var hashes = Hashes(Library());

        hashes.Libraries!["gadgets"].Should().Be("Fixture.Gadgets");
        hashes.Layers.Should().ContainKey("Model/Gadget.json");
        hashes.Layers!["Model/Gadget.json"].Keys.Should().BeEquivalentTo(["gadgets", "app"]);
        hashes.Version.Should().Be(2);
    }

    [Fact]
    public void A_library_update_that_changes_a_structure_fails_and_names_the_library()
    {
        var before = Hashes(Library());
        var after = Hashes(Library(serial: """, "isRequired": true"""));

        after.ModelHash.Should().NotBe(before.ModelHash, "a structural change in a library layer must fail --spark-verify-model");
        ModelHashVerifier.DescribeDrift(before, after).Should().Contain(line =>
            line.StartsWith("file Gadget.json:") && line.Contains("library 'gadgets' (Fixture.Gadgets) changed"));
    }

    [Fact]
    public void A_library_update_that_only_relabels_passes()
    {
        var before = Hashes(Library());
        var after = Hashes(Library(label: "Widget"));

        after.ModelHash.Should().Be(before.ModelHash, "a label is presentation; the gate is structural");
    }

    [Fact]
    public void The_application_editing_its_delta_names_the_application_not_the_library()
    {
        WriteApp(Path.Combine("Model", "Gadget.json"), """{ "persistentObject": { "name": "Gadget", "attributes": [ { "name": "Serial" } ] } }""");
        var before = Hashes(Library());
        WriteApp(Path.Combine("Model", "Gadget.json"), """{ "persistentObject": { "name": "Gadget", "attributes": [ { "name": "Serial", "isReadOnly": true } ] } }""");
        var after = Hashes(Library());

        var line = ModelHashVerifier.DescribeDrift(before, after).Should().ContainSingle(l => l.StartsWith("file Gadget.json:")).Which;
        line.Should().Contain("the application's file changed").And.NotContain("library 'gadgets'");
    }

    [Fact]
    public void A_type_a_library_newly_ships_says_so_rather_than_present_on_disk()
    {
        var before = Hashes();
        var after = Hashes(Library());

        ModelHashVerifier.DescribeDrift(before, after).Should().Contain(line =>
            line.StartsWith("file Gadget.json:") && line.Contains("shipped by library 'gadgets' (Fixture.Gadgets)") && !line.Contains("present on disk"));
        ModelHashVerifier.DescribeDrift(before, after).Should().Contain(line => line.StartsWith("library gadgets (Fixture.Gadgets)"));
    }

    [Fact]
    public void A_layer_that_moves_while_the_composed_structure_stays_still_fails_and_says_so()
    {
        // The application states isRequired; then the library ships the same value: composed alike.
        WriteApp(Path.Combine("Model", "Gadget.json"), """{ "persistentObject": { "name": "Gadget", "attributes": [ { "name": "Serial", "isRequired": true } ] } }""");
        var before = Hashes(Library());
        var after = Hashes(Library(serial: """, "isRequired": true"""));

        after.Files.Should().BeEquivalentTo(before.Files);
        after.ModelHash.Should().NotBe(before.ModelHash, "the layers are part of the roll-up, so the hash file cannot go stale against them");
        ModelHashVerifier.DescribeDrift(before, after).Should().Contain(line =>
            line.StartsWith("layers of Model/Gadget.json: the composed structure is unchanged") && line.Contains("library 'gadgets' (Fixture.Gadgets) changed"));
    }

    [Fact]
    public void A_library_changing_an_action_rule_fails_and_names_the_library()
    {
        var before = Hashes(Library(actions: """{ "Archive": { "selectionRule": "=1" } }"""));
        var after = Hashes(Library(actions: """{ "Archive": { "selectionRule": ">0" } }"""));

        after.ModelHash.Should().NotBe(before.ModelHash);
        ModelHashVerifier.DescribeDrift(before, after).Should().Contain(line =>
            line.StartsWith("config actions.json:") && line.Contains("library 'gadgets' (Fixture.Gadgets) changed"));
    }

    [Fact]
    public void A_library_removing_an_action_moves_its_layer_hash()
    {
        var keep = ConfigFileShape.DescribeLayerActions("""{ "Edit": {} }""");
        var remove = ConfigFileShape.DescribeLayerActions("""{ "Edit": null }""");

        remove.Should().NotBe(keep).And.Contain("removed");
    }

    // ---------- rights: the posture table ----------

    [Fact]
    public void The_security_drift_names_the_layers_whose_rows_or_identity_moved()
    {
        const string committed = """
            # Spark security posture
            ## Layers: libraries that ship rights (alias | assembly | hash)
            gadgets | Fixture.Gadgets | aaaaaaaaaaaa

            ## Reachable without signing in (expanded)
            (nothing)

            ## Rights
            Signed-in users (@authenticated) | grant | Read/Gadget | gadgets:read | gadgets
            Admins | grant | Edit/Car | a1 | app
            """;
        var current = committed
            .Replace("gadgets | Fixture.Gadgets | aaaaaaaaaaaa", "gadgets | Fixture.Gadgets | bbbbbbbbbbbb")
            .Replace("| Read/Gadget | gadgets:read |", "| QueryRead/Gadget | gadgets:read |");

        var (removed, added, layers) = SparkSecurityVerificationExtensions.Diff(committed, current);

        layers.Should().Equal(["gadgets (Fixture.Gadgets)"]);
        removed.Should().Contain("Signed-in users (@authenticated) | grant | Read/Gadget | gadgets:read | gadgets");
        added.Should().Contain("gadgets | Fixture.Gadgets | bbbbbbbbbbbb");
    }

    [Fact]
    public void The_posture_names_each_library_that_ships_rights_with_a_hash_of_what_it_states()
    {
        var library = new SparkLibrary("gadgets", "Fixture.Gadgets", [],
            [new SparkLibraryLayer("security", "security.json", """{ "rights": [ { "key": "read", "resource": "Read/Gadget", "groupId": "@authenticated" } ] }""")]);
        var reformatted = library with { Layers = [new SparkLibraryLayer("security", "security.json", """{"rights":[{"key":"read","resource":"Read/Gadget","groupId":"@authenticated"}]}""")] };
        var widened = library with { Layers = [new SparkLibraryLayer("security", "security.json", """{ "rights": [ { "key": "read", "resource": "Read/Gadget", "groupId": "@anonymous" } ] }""")] };

        var layer = Abstractions.Authorization.SparkSecurityFiles.Layers([library]).Should().ContainSingle().Which;

        layer.Alias.Should().Be("gadgets");
        layer.Assembly.Should().Be("Fixture.Gadgets");
        layer.Hash.Should().HaveLength(12);
        Abstractions.Authorization.SparkSecurityFiles.Layers([reformatted]).Single().Hash.Should().Be(layer.Hash, "whitespace is not a change");
        Abstractions.Authorization.SparkSecurityFiles.Layers([widened]).Single().Hash.Should().NotBe(layer.Hash);
    }
}
