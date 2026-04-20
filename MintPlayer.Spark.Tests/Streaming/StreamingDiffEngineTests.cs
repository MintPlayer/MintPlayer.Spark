using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Streaming;

namespace MintPlayer.Spark.Tests.Streaming;

public class StreamingDiffEngineTests
{
    private static readonly Guid PersonTypeId = Guid.NewGuid();

    private static PersistentObject Po(string id, params (string Name, object? Value)[] attrs) => new()
    {
        Id = id,
        Name = id,
        ObjectTypeId = PersonTypeId,
        Attributes = attrs.Select(a => new PersistentObjectAttribute { Name = a.Name, Value = a.Value }).ToArray(),
    };

    [Fact]
    public void First_call_returns_a_SnapshotMessage_carrying_all_items()
    {
        var engine = new StreamingDiffEngine();
        var items = new[] { Po("people/1", ("FirstName", "Alice")), Po("people/2", ("FirstName", "Bob")) };

        var message = engine.ComputeMessage(items);

        var snapshot = message.Should().BeOfType<SnapshotMessage>().Subject;
        snapshot.Data.Should().HaveCount(2);
        snapshot.Type.Should().Be("snapshot");
    }

    [Fact]
    public void Returns_null_when_a_second_call_has_no_changes()
    {
        var engine = new StreamingDiffEngine();
        var items = new[] { Po("people/1", ("FirstName", "Alice")) };

        engine.ComputeMessage(items);
        var secondMessage = engine.ComputeMessage(items);

        secondMessage.Should().BeNull();
    }

    [Fact]
    public void Changed_attribute_value_produces_a_PatchMessage_with_only_the_changed_attribute()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage([Po("people/1", ("FirstName", "Alice"), ("LastName", "Smith"))]);

        var patch = engine.ComputeMessage([Po("people/1", ("FirstName", "Alicia"), ("LastName", "Smith"))]);

        var patchMessage = patch.Should().BeOfType<PatchMessage>().Subject;
        patchMessage.Type.Should().Be("patch");
        patchMessage.Updated.Should().HaveCount(1);
        var item = patchMessage.Updated[0];
        item.Id.Should().Be("people/1");
        item.Attributes.Should().ContainKey("FirstName").WhoseValue.Should().Be("Alicia");
        item.Attributes.Should().NotContainKey("LastName");
    }

    [Fact]
    public void New_item_on_second_call_is_patched_with_all_of_its_attribute_values()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage([Po("people/1", ("FirstName", "Alice"))]);

        var patch = engine.ComputeMessage([
            Po("people/1", ("FirstName", "Alice")),
            Po("people/2", ("FirstName", "Bob"), ("LastName", "Jones")),
        ]);

        var patchMessage = patch.Should().BeOfType<PatchMessage>().Subject;
        patchMessage.Updated.Should().HaveCount(1);
        var newItem = patchMessage.Updated[0];
        newItem.Id.Should().Be("people/2");
        newItem.Attributes.Should().ContainKey("FirstName").WhoseValue.Should().Be("Bob");
        newItem.Attributes.Should().ContainKey("LastName").WhoseValue.Should().Be("Jones");
    }

    [Fact]
    public void New_attribute_appearing_on_an_existing_item_is_included_in_the_patch()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage([Po("people/1", ("FirstName", "Alice"))]);

        var patch = engine.ComputeMessage([
            Po("people/1", ("FirstName", "Alice"), ("LastName", "Smith")),
        ]);

        var patchMessage = patch.Should().BeOfType<PatchMessage>().Subject;
        var item = patchMessage.Updated.Single();
        item.Attributes.Should().ContainKey("LastName").WhoseValue.Should().Be("Smith");
    }

    [Fact]
    public void Items_with_null_ids_are_ignored_by_the_diff_state()
    {
        var engine = new StreamingDiffEngine();
        var nullIdItem = new PersistentObject
        {
            Id = null,
            Name = "anon",
            ObjectTypeId = PersonTypeId,
            Attributes = [new PersistentObjectAttribute { Name = "X", Value = "Y" }],
        };

        var first = engine.ComputeMessage([nullIdItem]);
        first.Should().BeOfType<SnapshotMessage>()
            .Which.Data.Should().ContainSingle().Which.Id.Should().BeNull();

        // Second call with only a keyed item — should be treated as a brand-new item patch.
        var second = engine.ComputeMessage([Po("people/1", ("FirstName", "Alice"))]);
        var patch = second.Should().BeOfType<PatchMessage>().Subject;
        patch.Updated.Should().ContainSingle().Which.Id.Should().Be("people/1");
    }

    [Fact]
    public void Null_and_null_compare_equal_no_patch_emitted_when_both_sides_are_null()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage([Po("people/1", ("Nickname", null))]);

        var secondMessage = engine.ComputeMessage([Po("people/1", ("Nickname", null))]);

        secondMessage.Should().BeNull();
    }

    [Fact]
    public void Null_to_value_counts_as_a_change_and_emits_a_patch()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage([Po("people/1", ("Nickname", null))]);

        var secondMessage = engine.ComputeMessage([Po("people/1", ("Nickname", "Ally"))]);

        var patch = secondMessage.Should().BeOfType<PatchMessage>().Subject;
        patch.Updated.Single().Attributes["Nickname"].Should().Be("Ally");
    }

    [Fact]
    public void State_advances_every_call_patches_are_computed_against_the_previous_call_not_the_snapshot()
    {
        var engine = new StreamingDiffEngine();
        engine.ComputeMessage([Po("people/1", ("FirstName", "Alice"))]);
        engine.ComputeMessage([Po("people/1", ("FirstName", "Alicia"))]);

        // Third call matches the second — no change relative to the second call
        var thirdMessage = engine.ComputeMessage([Po("people/1", ("FirstName", "Alicia"))]);

        thirdMessage.Should().BeNull();
    }
}
