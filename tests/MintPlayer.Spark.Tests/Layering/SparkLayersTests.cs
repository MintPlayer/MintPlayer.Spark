using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>The layering engine's rules (composition D2, D3, D15), one per test.</summary>
public class SparkLayersTests
{
    private static SparkLayer Library(string name, string json) => SparkLayer.Parse(name, json, isLibrary: true);

    private static SparkLayer App(string json) => SparkLayer.Parse("app", json, isLibrary: false);

    private static SparkComposition Compose(KindSpec spec, params SparkLayer[] layers) => SparkLayers.Compose(layers, spec);

    /// <summary>A kind with no special rules: ordinal keys, nothing atomic, nothing keyed.</summary>
    private static readonly KindSpec Plain = new("plain", StringComparer.Ordinal);

    private static string Json(SparkJsonNode? node) => node is null ? "<absent>" : SparkJson.Write(node);

    // ── Objects ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Objects_merge_per_property_and_keep_what_the_upper_layer_does_not_state()
    {
        var composition = Compose(Plain,
            Library("Lib", """{ "a": { "x": 1, "y": { "p": "lib", "q": "lib" } } }"""),
            App("""{ "a": { "y": { "q": "app" }, "z": true } }"""));

        Json(composition.Result).Should().Be("""{"a":{"x":1,"y":{"p":"lib","q":"app"},"z":true}}""");
        composition.Provenance["a.x"].Should().Be("Lib");
        composition.Provenance["a.y.p"].Should().Be("Lib");
        composition.Provenance["a.y.q"].Should().Be("app");
        composition.Provenance["a.z"].Should().Be("app");
        composition.Provenance["a"].Should().Be("Lib", "the object was declared by the library");
    }

    [Fact]
    public void Null_removes_what_the_layers_below_state()
    {
        var composition = Compose(Plain,
            Library("Lib", """{ "a": { "x": 1, "y": 2 }, "b": {} }"""),
            App("""{ "a": { "x": null }, "b": null }"""));

        Json(composition.Result).Should().Be("""{"a":{"y":2}}""");
        composition.Provenance.Should().NotContainKey("a.x");
    }

    [Fact]
    public void A_member_reset_with_null_and_set_again_by_a_later_layer_is_appended()
    {
        // Decided in D2: the hand-written actions engine kept the old position (Dictionary slot reuse).
        var composition = Compose(Plain,
            Library("Core", """{ "a": { "x": 1, "y": 2, "z": 3 } }"""),
            Library("Lib", """{ "a": { "x": null } }"""),
            App("""{ "a": { "x": 4 } }"""));

        Json(composition.Result).Should().Be("""{"a":{"y":2,"z":3,"x":4}}""");
        composition.Provenance["a.x"].Should().Be("app");
    }

    [Fact]
    public void A_value_in_place_is_replaced_without_moving()
    {
        var composition = Compose(Plain,
            Library("Lib", """{ "x": 1, "y": 2 }"""),
            App("""{ "x": 3 }"""));

        Json(composition.Result).Should().Be("""{"x":3,"y":2}""");
    }

    [Fact]
    public void Annotations_are_never_data()
    {
        var composition = Compose(Plain,
            Library("Lib", """{ "$schema": "lib.json", "_comment": ["a", "b"], "a": { "_note": 1, "x": 1 } }"""),
            App("""{ "$schema": "app.json", "a": { "_note": { "deep": true } } }"""));

        Json(composition.Result).Should().Be("""{"a":{"x":1}}""");
        composition.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void A_literal_null_layer_states_nothing()
    {
        var composition = Compose(Plain, Library("Lib", """{ "x": 1 }"""), App("null"));

        Json(composition.Result).Should().Be("""{"x":1}""");
    }

    // ── Arrays ──────────────────────────────────────────────────────────────────────────────────

    private static readonly KindSpec Keyed = new("keyed", StringComparer.Ordinal,
        arrayKey: path => path[path.Count - 1] == "items" ? "name" : null);

    [Fact]
    public void A_keyed_array_merges_per_element_key()
    {
        var composition = Compose(Keyed,
            Library("Lib", """{ "items": [ { "name": "A", "x": 1, "y": 1 }, { "name": "B", "x": 1 } ] }"""),
            App("""{ "items": [ { "name": "B", "x": 2 }, { "name": "C", "x": 3 } ] }"""));

        Json(composition.Result).Should().Be(
            """{"items":[{"name":"A","x":1,"y":1},{"name":"B","x":2},{"name":"C","x":3}]}""");
        composition.Provenance["items[A].y"].Should().Be("Lib");
        composition.Provenance["items[B].x"].Should().Be("app");
        composition.Provenance["items[B]"].Should().Be("Lib", "the element was declared by the library");
        composition.Provenance["items[C]"].Should().Be("app");
    }

    [Fact]
    public void Remove_true_removes_a_keyed_element_and_a_later_layer_can_add_it_back_at_the_end()
    {
        var composition = Compose(Keyed,
            Library("Core", """{ "items": [ { "name": "A", "x": 1 }, { "name": "B", "x": 1 } ] }"""),
            Library("Lib", """{ "items": [ { "name": "A", "$remove": true } ] }"""),
            App("""{ "items": [ { "name": "A", "x": 9 } ] }"""));

        Json(composition.Result).Should().Be("""{"items":[{"name":"B","x":1},{"name":"A","x":9}]}""");
    }

    [Fact]
    public void Remove_with_anything_besides_the_key_is_refused()
    {
        var act = () => Compose(Keyed,
            Library("Lib", """{ "items": [ { "name": "A", "x": 1 } ] }"""),
            App("""{ "items": [ { "name": "A", "x": 2, "$remove": true } ] }"""));

        act.Should().Throw<SparkLayerException>().Which.Message.Should().Contain("app: 'items[A]'").And.Contain("$remove");
    }

    [Fact]
    public void A_keyed_element_without_its_key_is_refused()
    {
        var act = () => Compose(Keyed, App("""{ "items": [ { "x": 1 } ] }"""));

        act.Should().Throw<SparkLayerException>().WithMessage("*items*string 'name'*");
    }

    [Fact]
    public void An_array_of_primitives_is_replaced_whole()
    {
        var composition = Compose(Plain,
            Library("Lib", """{ "tags": ["a", "b", "c"] }"""),
            App("""{ "tags": ["d"] }"""));

        Json(composition.Result).Should().Be("""{"tags":["d"]}""");
        composition.Provenance["tags"].Should().Be("app");
    }

    // ── Atomic and immutable paths ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_atomic_path_is_replaced_whole_not_merged()
    {
        var composition = Compose(SparkKinds.Actions,
            Library("Lib", """{ "Archive": { "label": { "en": "Archive", "nl": "Archiveren" } } }"""),
            App("""{ "Archive": { "label": { "fr": "Archiver" } } }"""));

        Json(composition.Result).Should().Be("""{"Archive":{"label":{"fr":"Archiver"}}}""");
        composition.Provenance["Archive.label"].Should().Be("app");
    }

    [Fact]
    public void An_immutable_id_cannot_be_changed_and_the_change_is_reported()
    {
        var composition = Compose(SparkKinds.Model,
            Library("Lib", """{ "persistentObject": { "id": "po-1", "name": "Car", "attributes": [ { "id": "at-1", "name": "Plate" } ] } }"""),
            App("""{ "persistentObject": { "id": "po-2", "attributes": [ { "id": "at-2", "name": "Plate" } ] } }"""));

        Json(composition.Result).Should().Be(
            """{"persistentObject":{"id":"po-1","name":"Car","attributes":[{"id":"at-1","name":"Plate"}]}}""");
        composition.Errors.Select(e => e.Path).Should().Equal("persistentObject.id", "persistentObject.attributes[Plate].id");
        composition.Errors.Should().OnlyContain(e => e.Layer == "app");
    }

    [Fact]
    public void Restating_an_immutable_id_unchanged_is_no_error()
    {
        var composition = Compose(SparkKinds.Model,
            Library("Lib", """{ "persistentObject": { "id": "po-1", "name": "Car" } }"""),
            App("""{ "persistentObject": { "id": "po-1", "name": "Car" } }"""));

        composition.Errors.Should().BeEmpty();
    }

    // ── Conflicts ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_libraries_stating_a_leaf_differently_conflict_naming_both_the_later_wins()
    {
        var composition = Compose(Plain,
            Library("A.Lib", """{ "a": { "x": 1 } }"""),
            Library("B.Lib", """{ "a": { "x": 2 } }"""));

        Json(composition.Result).Should().Be("""{"a":{"x":2}}""");
        var conflict = composition.Conflicts.Should().ContainSingle().Which;
        conflict.PathText.Should().Be("a.x");
        conflict.WinnerLayer.Should().Be("B.Lib");
        conflict.LoserLayer.Should().Be("A.Lib");
    }

    [Fact]
    public void Two_libraries_stating_the_same_value_do_not_conflict()
    {
        var composition = Compose(Plain,
            Library("A.Lib", """{ "a": { "x": 1.0, "y": { "p": 1, "q": 2 } } }"""),
            Library("B.Lib", """{ "a": { "x": 1, "y": { "q": 2, "p": 1 } } }"""));

        composition.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void The_app_overriding_a_library_wins_silently()
    {
        var composition = Compose(Plain,
            Library("A.Lib", """{ "a": { "x": 1 } }"""),
            Library("B.Lib", """{ "a": { "x": 2 } }"""),
            App("""{ "a": { "x": 3 } }"""));

        Json(composition.Result).Should().Be("""{"a":{"x":3}}""");
        // Only the two libraries conflict, never the app.
        composition.Conflicts.Should().ContainSingle().Which.LoserLayer.Should().Be("A.Lib");
    }

    [Fact]
    public void A_conflict_inside_a_keyed_element_names_the_element()
    {
        var composition = Compose(Keyed,
            Library("A.Lib", """{ "items": [ { "name": "A", "x": 1 } ] }"""),
            Library("B.Lib", """{ "items": [ { "name": "A", "x": 2 } ] }"""));

        composition.Conflicts.Should().ContainSingle().Which.PathText.Should().Be("items[A].x");
    }

    // ── Keys ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Action_keys_ignore_case_and_keep_the_first_spelling()
    {
        var composition = Compose(SparkKinds.Actions,
            Library("Lib", """{ "Archive": { "icon": "box" } }"""),
            App("""{ "ARCHIVE": { "ICON": "z", "Variant": "danger" } }"""));

        Json(composition.Result).Should().Be("""{"Archive":{"icon":"z","Variant":"danger"}}""");
    }

    [Fact]
    public void Ordinal_keys_keep_differently_cased_members_apart()
    {
        var composition = Compose(SparkKinds.Model,
            Library("Lib", """{ "persistentObject": { "attributes": [ { "name": "Name", "order": 1 } ] } }"""),
            App("""{ "persistentObject": { "attributes": [ { "name": "name", "order": 2 } ] } }"""));

        Json(composition.Result).Should().Be(
            """{"persistentObject":{"attributes":[{"name":"Name","order":1},{"name":"name","order":2}]}}""");
    }

    [Fact]
    public void A_key_stated_twice_in_one_layer_under_the_kinds_comparer_is_refused()
    {
        var act = () => Compose(SparkKinds.Actions, App("""{ "Archive": {}, "archive": {} }"""));

        act.Should().Throw<SparkLayerException>().WithMessage("*app*archive*twice*case-insensitive*");
    }

    [Fact]
    public void An_action_that_is_not_an_object_or_null_is_refused()
    {
        var act = () => Compose(SparkKinds.Actions, App("""{ "Archive": "yes" }"""));

        act.Should().Throw<SparkLayerException>().WithMessage("*app*'Archive' must be an object*");
    }

    // ── Per-kind flags ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_string_from_the_app_means_untranslated_when_the_kind_says_so()
    {
        var untranslated = new KindSpec("texts", StringComparer.Ordinal, appEmptyStringIsUntranslated: true);

        var composition = Compose(untranslated,
            Library("Lib", """{ "a": { "en": "Archive", "nl": "" } }"""),
            App("""{ "a": { "en": "", "fr": "" } }"""));

        Json(composition.Result).Should().Be("""{"a":{"en":"Archive","nl":""}}""", "a library's \"\" is kept, the app's is ignored");
        Json(Compose(Plain, Library("Lib", """{ "a": "x" }"""), App("""{ "a": "" }""")).Result)
            .Should().Be("""{"a":""}""", "elsewhere \"\" is a value");
    }

    [Fact]
    public void A_kind_without_null_removal_reports_it_and_keeps_the_value()
    {
        var keepAll = new KindSpec("keep", StringComparer.Ordinal,
            arrayKey: path => path[path.Count - 1] == "items" ? "name" : null, nullRemoves: false);

        var composition = Compose(keepAll,
            Library("Lib", """{ "x": 1, "items": [ { "name": "A" } ] }"""),
            App("""{ "x": null, "items": [ { "name": "A", "$remove": true } ] }"""));

        Json(composition.Result).Should().Be("""{"x":1,"items":[{"name":"A"}]}""");
        composition.Errors.Select(e => e.Path).Should().Equal("x", "items[A]");
    }

    // ── Model delta ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_app_delta_over_a_library_model_overrides_showedOn_and_keeps_every_library_field()
    {
        var library = Library("MintPlayer.Spark.Authorization", """
            {
              "$schema": "../../schemas/model.schema.json",
              "_comment": ["shipped by the library"],
              "persistentObject": {
                "id": "4e13c0de-0000-4000-8000-000000000001",
                "name": "SparkUser",
                "clrType": "MintPlayer.Spark.Authorization.Identity.SparkUser",
                "breadcrumb": "{UserName}",
                "tabs": [],
                "groups": [],
                "attributes": [
                  { "id": "4e13c0de-0000-4000-8000-000000000011", "name": "UserName", "dataType": "string",
                    "isReadOnly": true, "order": 1, "showedOn": "PersistentObject", "rules": [] }
                ],
                "queries": []
              },
              "queries": []
            }
            """);
        var delta = App("""
            {
              "persistentObject": {
                "attributes": [
                  { "name": "UserName", "showedOn": "Query, PersistentObject" },
                  { "id": "4e13c0de-0000-4000-8000-0000000000aa", "name": "DisplayName", "dataType": "string", "order": 2 }
                ]
              }
            }
            """);

        var composition = Compose(SparkKinds.Model, library, delta);

        composition.Errors.Should().BeEmpty();
        var po = (SparkJsonObject)composition.Result["persistentObject"]!;
        Json(po["clrType"]).Should().Be("\"MintPlayer.Spark.Authorization.Identity.SparkUser\"");
        Json(po["breadcrumb"]).Should().Be("\"{UserName}\"");
        var attributes = ((SparkJsonArray)po["attributes"]!).Items.Cast<SparkJsonObject>().ToList();
        attributes.Select(a => Json(a["name"])).Should().Equal("\"UserName\"", "\"DisplayName\"");
        Json(attributes[0]).Should().Be(
            """{"id":"4e13c0de-0000-4000-8000-000000000011","name":"UserName","dataType":"string","isReadOnly":true,"order":1,"showedOn":"Query, PersistentObject","rules":[]}""");

        composition.Provenance["persistentObject.attributes[UserName].showedOn"].Should().Be("app");
        composition.Provenance["persistentObject.attributes[UserName].isReadOnly"].Should().Be("MintPlayer.Spark.Authorization");
        composition.Provenance["persistentObject.attributes[UserName]"].Should().Be("MintPlayer.Spark.Authorization");
        composition.Provenance["persistentObject.attributes[DisplayName]"].Should().Be("app");
    }
}
