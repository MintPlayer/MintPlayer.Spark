using System.Text.Json.Nodes;
using MintPlayer.Spark.Live;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// The patches live updates send (X4, D24), computed on the wire form. Replaces the streaming diff engine's
/// tests and adds what it could not do: removed rows (defect 1), order, and equality of arrays and objects
/// (the old engine compared boxed values with <c>object.Equals</c>, which compares those by reference).
/// </summary>
public class LiveDiffTests
{
    private static JsonObject Page(params (string Id, object? Name)[] rows) => Page(rows.Length, rows);

    private static JsonObject Page(int total, params (string Id, object? Name)[] rows) => new()
    {
        ["columns"] = new JsonArray(new JsonObject { ["name"] = "Name" }),
        ["items"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id,
            ["values"] = new JsonArray(new JsonObject { ["key"] = "Name", ["value"] = JsonValue(r.Name) }),
        })]),
        ["totalItems"] = total,
        ["skip"] = 0,
        ["take"] = 50,
    };

    private static JsonNode? JsonValue(object? value) => value switch
    {
        null => null,
        string s => s,
        string[] a => new JsonArray([.. a.Select(x => (JsonNode)x)]),
        _ => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value)),
    };

    [Fact]
    public void The_first_page_is_a_full_result()
    {
        var diff = LiveDiff.Query(null, Page(("a", "A")));

        diff!.Value.Type.Should().Be("result");
    }

    [Fact]
    public void An_unchanged_page_sends_nothing()
        => LiveDiff.Query(Page(("a", "A"), ("b", "B")), Page(("a", "A"), ("b", "B"))).Should().BeNull();

    [Fact]
    public void A_changed_row_is_upserted_whole_and_nothing_else_is_sent()
    {
        var (type, payload) = LiveDiff.Query(Page(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D")), Page(("a", "A"), ("b", "B2"), ("c", "C"), ("d", "D")))!.Value;

        type.Should().Be("patch");
        payload["upsert"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).Should().Equal("b");
        payload.AsObject().ContainsKey("remove").Should().BeFalse();
        payload.AsObject().ContainsKey("order").Should().BeFalse("the order did not change");
        payload.AsObject().ContainsKey("set").Should().BeFalse();
    }

    [Fact]
    public void A_row_that_left_the_set_is_removed()
    {
        // Defect 1 of the streaming queries this replaces: a row that left the set stayed on the client forever.
        var (type, payload) = LiveDiff.Query(
            Page(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D"), ("e", "E")),
            Page(4, ("a", "A"), ("c", "C"), ("d", "D"), ("e", "E")))!.Value;

        type.Should().Be("patch");
        payload["remove"]!.AsArray().Select(i => i!.GetValue<string>()).Should().Equal("b");
        payload.AsObject().ContainsKey("order").Should().BeFalse("removing alone leaves the order the client computes");
        payload["set"]!["totalItems"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void A_new_row_is_upserted_with_the_new_order()
    {
        var (type, payload) = LiveDiff.Query(
            Page(("a", "A"), ("c", "C"), ("d", "D"), ("e", "E")),
            Page(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D"), ("e", "E")))!.Value;

        type.Should().Be("patch");
        payload["upsert"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).Should().Equal("b");
        payload["order"]!.AsArray().Select(i => i!.GetValue<string>()).Should().Equal("a", "b", "c", "d", "e");
    }

    [Fact]
    public void A_reordered_page_sends_only_the_order()
    {
        var (_, payload) = LiveDiff.Query(
            Page(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D"), ("e", "E"), ("f", "F")),
            Page(("b", "B"), ("a", "A"), ("c", "C"), ("d", "D"), ("e", "E"), ("f", "F")))!.Value;

        payload.AsObject().ContainsKey("upsert").Should().BeFalse();
        payload["order"]!.AsArray().Select(i => i!.GetValue<string>()).Should().Equal("b", "a", "c", "d", "e", "f");
    }

    [Fact]
    public void Equal_arrays_are_equal_and_a_changed_element_is_a_change()
    {
        // The old engine compared boxed values by reference: an array-valued cell re-patched every batch.
        LiveDiff.Query(Page(("a", new[] { "x", "y" }), ("b", "B")), Page(("a", new[] { "x", "y" }), ("b", "B"))).Should().BeNull();
        LiveDiff.Query(Page(("a", new[] { "x", "y" }), ("b", "B"), ("c", "C")), Page(("a", new[] { "x", "z" }), ("b", "B"), ("c", "C")))!.Value.Type.Should().Be("patch");
    }

    [Fact]
    public void Changed_columns_send_a_full_result()
    {
        var next = Page(("a", "A"));
        next["columns"] = new JsonArray(new JsonObject { ["name"] = "Name" }, new JsonObject { ["name"] = "Owner" });

        LiveDiff.Query(Page(("a", "A")), next)!.Value.Type.Should().Be("result");
    }

    [Fact]
    public void A_page_that_turned_over_is_sent_whole_when_the_patch_would_not_be_smaller()
        => LiveDiff.Query(
            Page(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D"), ("e", "E")),
            Page(("f", "F"), ("g", "G"), ("h", "H"), ("i", "I"), ("j", "J")))!.Value.Type.Should().Be("result");

    // ---- persistent objects ----------------------------------------------------------------------------

    private static JsonObject Po(string etag, params (string Name, object? Value)[] attributes) => new()
    {
        ["id"] = "notes/1",
        ["etag"] = etag,
        ["breadcrumb"] = "Note",
        ["attributes"] = new JsonArray([.. attributes.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["value"] = JsonValue(a.Value) })]),
    };

    [Fact]
    public void A_changed_attribute_is_patched_with_the_new_etag()
    {
        var (type, payload) = LiveDiff.PersistentObject(Po("1", ("Title", "a"), ("Body", "b")), Po("2", ("Title", "A"), ("Body", "b")))!.Value;

        type.Should().Be("poPatch");
        payload["attributes"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).Should().Equal("Title");
        payload["set"]!["etag"]!.GetValue<string>().Should().Be("2");
    }

    [Fact]
    public void An_attribute_that_appears_or_goes_sends_the_whole_object()
        => LiveDiff.PersistentObject(Po("1", ("Title", "a"), ("Body", "b")), Po("1", ("Title", "a")))!.Value.Type.Should().Be("po");

    [Fact]
    public void An_unchanged_object_sends_nothing()
        => LiveDiff.PersistentObject(Po("1", ("Title", "a")), Po("1", ("Title", "a"))).Should().BeNull();
}
