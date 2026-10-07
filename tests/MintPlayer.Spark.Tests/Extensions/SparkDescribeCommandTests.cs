using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// <c>--spark-describe &lt;kind&gt; [name] [--layers]</c> (composition D9): the composed result of every
/// kind, one element of it, and with <c>--layers</c> the layer each leaf came from — a library by its
/// alias, the application as <c>app</c>.
/// </summary>
public sealed class SparkDescribeCommandTests : IDisposable
{
    private readonly string _root;

    public SparkDescribeCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "spark-describe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "App_Data", "Model"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private const string SignedIn = "00000000-0000-0000-0000-000000000001";

    /// <summary>A library that ships one layer of every kind a library may ship.</summary>
    private static readonly SparkLibrary Gadgets = new("gadgets", "Fixture.Gadgets", [],
    [
        new SparkLibraryLayer("actions", "actions.json", """{ "Archive": { "icon": "box", "selectionRule": "=1" } }"""),
        new SparkLibraryLayer("model", "Model/Gadget.json", """
            { "persistentObject": { "id": "36b0f3e1-2a4c-5d9e-8f10-112233445566", "name": "Gadget",
              "attributes": [ { "id": "46b0f3e1-2a4c-5d9e-8f10-112233445566", "name": "Serial", "dataType": "string", "showedOn": "Query, PersistentObject" } ] } }
            """),
        new SparkLibraryLayer("translations", "translations.json", """{ "model": { "Gadget": { "label": { "en": "Gadget", "nl": "Toestel" } } } }"""),
        new SparkLibraryLayer("security", "security.json", """{ "rights": [ { "key": "read", "resource": "Read/Gadget", "groupId": "@authenticated" } ] }"""),
        new SparkLibraryLayer("programUnits", "programUnits.json", """
            { "programUnitGroups": [ { "id": "aaaaaaaa-0000-4000-8000-000000000001", "name": "programUnits.groups.gadgets", "order": 5,
              "programUnits": [ { "id": "aaaaaaaa-0000-4000-8000-000000000002", "name": "programUnits.gadgets", "type": "query", "queryId": "aaaaaaaa-0000-4000-8000-000000000003", "order": 1 } ] } ] }
            """),
        new SparkLibraryLayer("moderation", "moderation.json", """{ "Reputation": { "Upvoted": 10, "Downvoted": -2 } }"""),
    ]);

    private void WriteApp(string relative, string json) => File.WriteAllText(Path.Combine(_root, "App_Data", relative), json);

    private string Describe(string kind, string? name = null, bool layers = false)
        => SparkDescribeCommand.Describe(_root, kind, name, layers, [Gadgets]);

    [Fact]
    public void Parsing_takes_the_kind_and_name_after_the_flag_and_layers_anywhere()
    {
        SparkDescribeCommand.TryParse(["--spark-describe", "model", "Gadget", "--layers"], out var kind, out var name, out var layers).Should().BeTrue();
        kind.Should().Be("model");
        name.Should().Be("Gadget");
        layers.Should().BeTrue();

        SparkDescribeCommand.TryParse(["--layers", "--spark-describe", "actions"], out kind, out name, out layers).Should().BeTrue();
        kind.Should().Be("actions");
        name.Should().BeNull();
        layers.Should().BeTrue();

        SparkDescribeCommand.TryParse(["--spark-verify-model"], out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_or_missing_kind_lists_the_kinds()
    {
        var unknown = () => Describe("widgets");
        unknown.Should().Throw<ArgumentException>().WithMessage("*actions, model, translations, security, programUnits, moderation*");

        var missing = () => SparkDescribeCommand.Describe(_root, null, null, false, [Gadgets]);
        missing.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actions_print_the_composed_json_and_with_layers_each_leaf_and_its_layer()
    {
        WriteApp("actions.json", """{ "Archive": { "icon": "archive" } }""");

        var composed = Describe("actions", "Archive");
        composed.Should().Contain("gadgets (Fixture.Gadgets) → app (App_Data/actions.json)");
        composed.Should().Contain("\"icon\": \"archive\"").And.Contain("\"selectionRule\": \"=1\"").And.NotContain("@");

        var layered = Describe("actions", "Archive", layers: true);
        layered.Should().Contain("Archive.icon = \"archive\" @app");
        layered.Should().Contain("Archive.selectionRule = \"=1\" @gadgets");
    }

    [Fact]
    public void Model_shows_a_library_type_with_the_application_delta_on_top()
    {
        WriteApp(Path.Combine("Model", "Gadget.json"), """{ "persistentObject": { "name": "Gadget", "attributes": [ { "name": "Serial", "showedOn": "PersistentObject" } ] } }""");

        var layered = Describe("model", "Gadget", layers: true);

        layered.Should().Contain("model Gadget, composed from: gadgets (Fixture.Gadgets) → app (");
        layered.Should().Contain("persistentObject.attributes[Serial].showedOn = \"PersistentObject\" @app");
        layered.Should().Contain("persistentObject.attributes[Serial].dataType = \"string\" @gadgets");
    }

    [Fact]
    public void Model_shows_an_application_type_as_one_layer()
    {
        WriteApp(Path.Combine("Model", "Car.json"), """{ "persistentObject": { "id": "11111111-1111-1111-1111-111111111111", "name": "Car", "attributes": [] } }""");

        Describe("model", "Car", layers: true).Should().Contain("persistentObject.name = \"Car\" @app");

        var unknown = () => Describe("model", "Boat");
        unknown.Should().Throw<ArgumentException>().WithMessage("*'Boat'*");
    }

    [Fact]
    public void Translations_narrow_by_key_prefix()
    {
        WriteApp("translations.json", """{ "model": { "Gadget": { "label": { "nl": "Apparaat" } } }, "common": { "ok": { "en": "OK" } } }""");

        var layered = Describe("translations", "model.Gadget", layers: true);

        layered.Should().Contain("model.Gadget.label.en = \"Gadget\" @gadgets");
        layered.Should().Contain("model.Gadget.label.nl = \"Apparaat\" @app");
        layered.Should().NotContain("common.ok");
    }

    [Fact]
    public void Security_prints_the_resolved_rights_and_with_layers_the_layer_column()
    {
        WriteApp("security.json", $$"""
            { "wellKnown": { "authenticated": "{{SignedIn}}" }, "groups": { "{{SignedIn}}": "Signed-in users" },
              "rights": [ { "key": "own", "resource": "Edit/Gadget", "groupId": "{{SignedIn}}" } ] }
            """);

        var table = Describe("security");
        table.Should().Contain("key | resource | group | effect\n".Replace("\n", Environment.NewLine));
        table.Should().Contain("gadgets:read | Read/Gadget | Signed-in users (@authenticated) | grant");

        var layered = Describe("rights", "Read/Gadget", layers: true);
        layered.Should().Contain("gadgets:read | Read/Gadget | Signed-in users (@authenticated) | grant | gadgets");
        layered.Should().NotContain("Edit/Gadget");
    }

    [Fact]
    public void Program_units_merge_by_id_and_narrow_to_one_unit()
    {
        WriteApp("programUnits.json", """
            { "programUnitGroups": [ { "id": "aaaaaaaa-0000-4000-8000-000000000001", "programUnits": [ { "id": "aaaaaaaa-0000-4000-8000-000000000002", "order": 9 } ] } ] }
            """);

        var unit = Describe("programUnits", "aaaaaaaa-0000-4000-8000-000000000002");
        unit.Should().Contain("\"order\": 9").And.Contain("\"type\": \"query\"").And.NotContain("programUnits.groups.gadgets");

        var layered = Describe("programUnits", "aaaaaaaa-0000-4000-8000-000000000002", layers: true);
        layered.Should().Contain("[aaaaaaaa-0000-4000-8000-000000000002].order = 9 @app");
        layered.Should().Contain("[aaaaaaaa-0000-4000-8000-000000000002].type = \"query\" @gadgets");
    }

    [Fact]
    public void Moderation_shows_the_library_defaults_under_the_application_overrides()
    {
        WriteApp("moderation.json", """{ "Reputation": { "Downvoted": -5 } }""");

        var layered = Describe("moderation", "Reputation", layers: true);

        layered.Should().Contain("Reputation.Upvoted = 10 @gadgets");
        layered.Should().Contain("Reputation.Downvoted = -5 @app");
    }
}
