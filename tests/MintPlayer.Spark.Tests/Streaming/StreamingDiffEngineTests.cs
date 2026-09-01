using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Streaming;

namespace MintPlayer.Spark.Tests.Streaming;

public class StreamingDiffEngineTests
{
    private static readonly Guid PersonTypeId = Guid.NewGuid();

    /// <summary>One streamed row: id plus a value per column, no metadata (#327 M4).</summary>
    private static QueryResultItem Po(string id, params (string Name, object? Value)[] attrs) => new()
    {
        Id = id,
        Breadcrumb = id,
        Values = [.. attrs.Select(a => new QueryResultItemValue { Key = a.Name, Value = a.Value })],
    };

    /// <summary>The stream shape is fixed when it opens, so every batch carries the same columns.</summary>
    private static readonly IReadOnlyList<QueryColumn> Columns =
    [
        new QueryColumn { Name = "FirstName", DataType = "string" },
        new QueryColumn { Name = "LastName", DataType = "string" },
    ];

    private static StreamingQueryBatch Batch(params QueryResultItem[] items) => new(Columns, items);

    [Fact]
    public void First_call_returns_a_SnapshotMessage_carrying_all_items()
    {
        var engine = new StreamingDiffEngine();
        var items = new[] { Po("people/1", ("FirstName", "Alice")), Po("people/2", ("FirstName", "Bob")) };

        var message = engine.ComputeMessage(Batch(items));

        var snapshot = message.Should().BeOfType<SnapshotMessage>().Which;
        snapshot.Data.Should().HaveCount(2);
        snapshot.Type.Should().Be("snapshot");
    }

    [Fact]
    public void Returns_null_when_a_second_call_has_no_changes()
    {
        var engine = new StreamingDiffEngine();
        var items = new[] { Po("people/1", ("FirstName", "Alice")) };

        engine.ComputeMessage(Batch(items));
        var secondMessage = engine.ComputeMessage(Batch(items));

        secondMessage.Should().BeNull();
    }

    [Fact]
    public void Changed_attribute_value_produces_a_PatchMessage_with_only_the_changed_attribute()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alice"), ("LastName", "Smith"))));

        var patch = engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alicia"), ("LastName", "Smith"))));

        var patchMessage = patch.Should().BeOfType<PatchMessage>().Which;
        patchMessage.Type.Should().Be("patch");
        patchMessage.Updated.Should().HaveCount(1);
        var item = patchMessage.Updated[0];
        item.Id.Should().Be("people/1");
        item.Values.Should().ContainKey("FirstName").Which.Should().Be("Alicia");
        item.Values.Should().NotContainKey("LastName");
    }

    [Fact]
    public void New_item_on_second_call_is_patched_with_all_of_its_attribute_values()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alice"))));

        var patch = engine.ComputeMessage(Batch(
            Po("people/1", ("FirstName", "Alice")),
            Po("people/2", ("FirstName", "Bob"), ("LastName", "Jones"))
        ));

        var patchMessage = patch.Should().BeOfType<PatchMessage>().Which;
        patchMessage.Updated.Should().HaveCount(1);
        var newItem = patchMessage.Updated[0];
        newItem.Id.Should().Be("people/2");
        newItem.Values.Should().ContainKey("FirstName").Which.Should().Be("Bob");
        newItem.Values.Should().ContainKey("LastName").Which.Should().Be("Jones");
    }

    [Fact]
    public void New_attribute_appearing_on_an_existing_item_is_included_in_the_patch()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alice"))));

        var patch = engine.ComputeMessage(Batch(
            Po("people/1", ("FirstName", "Alice"), ("LastName", "Smith"))
        ));

        var patchMessage = patch.Should().BeOfType<PatchMessage>().Which;
        var item = patchMessage.Updated.Single();
        item.Values.Should().ContainKey("LastName").Which.Should().Be("Smith");
    }

    [Fact]
    public void A_row_the_client_has_not_seen_is_patched_in_full()
    {
        // This replaces a test for null-id rows being skipped by the diff state. That case cannot
        // reach the engine any more: the projection refuses a row with no id, so a stream fails
        // loudly at the source instead of delivering a snapshot and then never updating those rows
        // — which is what "ignored by the diff state" actually meant for a client (#327 M4).
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alice"))));

        var second = engine.ComputeMessage(Batch(
            Po("people/1", ("FirstName", "Alice")),
            Po("people/2", ("FirstName", "Bob"))
        ));

        var patch = second.Should().BeOfType<PatchMessage>().Which;
        var added = patch.Updated.Should().ContainSingle().Which;
        added.Id.Should().Be("people/2");
        added.Values.Should().ContainKey("FirstName").Which.Should().Be("Bob",
            "a row the client has never seen needs every value, not a diff against nothing");
    }

    [Fact]
    public void Null_and_null_compare_equal_no_patch_emitted_when_both_sides_are_null()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("Nickname", null))));

        var secondMessage = engine.ComputeMessage(Batch(Po("people/1", ("Nickname", null))));

        secondMessage.Should().BeNull();
    }

    [Fact]
    public void Null_to_value_counts_as_a_change_and_emits_a_patch()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("Nickname", null))));

        var secondMessage = engine.ComputeMessage(Batch(Po("people/1", ("Nickname", "Ally"))));

        var patch = secondMessage.Should().BeOfType<PatchMessage>().Which;
        patch.Updated.Single().Values["Nickname"].Should().Be("Ally");
    }

    [Fact]
    public void State_advances_every_call_patches_are_computed_against_the_previous_call_not_the_snapshot()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alice"))));
        engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alicia"))));

        // Third call matches the second — no change relative to the second call
        var thirdMessage = engine.ComputeMessage(Batch(Po("people/1", ("FirstName", "Alicia"))));

        thirdMessage.Should().BeNull();
    }
}
