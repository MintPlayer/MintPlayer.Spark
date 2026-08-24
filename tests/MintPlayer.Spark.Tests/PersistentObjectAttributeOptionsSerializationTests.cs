using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// <c>PersistentObjectAttribute</c> is serialized by a hand-written converter, so adding a property
/// to the class does nothing on its own: the field has to be added in three separate places — the
/// read switch, the write body, and the <c>KnownFieldNames</c> array used to map wire names back to
/// CLR names. Missing any one of them fails <em>silently</em>. Miss the write and the field never
/// leaves the server; miss the read and it never arrives; miss the name array and it survives a
/// camelCase producer but not a PascalCase one.
///
/// <para>
/// These tests exist because "I added the property" is not evidence that any of that happened.
/// </para>
/// </summary>
public class PersistentObjectAttributeOptionsSerializationTests
{
    private static PersistentObject WithOptions(IReadOnlyList<PersistentObjectAttributeOption>? options) =>
        new()
        {
            Name = "Car",
            ObjectTypeId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Attributes =
            [
                new PersistentObjectAttribute
                {
                    Name = "Status",
                    DataType = "LookupReference",
                    Options = options,
                },
            ],
        };

    [Fact]
    public void Options_survive_a_round_trip()
    {
        var original = WithOptions(
        [
            new PersistentObjectAttributeOption { Key = "Stolen", Label = TranslatedString.Create("Stolen") },
            new PersistentObjectAttributeOption { Key = "InUse" },
        ]);

        var roundTripped = JsonSerializer.Deserialize<PersistentObject>(JsonSerializer.Serialize(original))!;

        var options = roundTripped.Attributes[0].Options;
        options.Should().NotBeNull();
        options!.Select(o => o.Key).Should().Equal(["Stolen", "InUse"]);
        options[0].Label!.GetDefaultValue().Should().Be("Stolen");
        options[1].Label.Should().BeNull("a label is optional; the client falls back to the key");
    }

    [Fact]
    public void Options_round_trip_through_a_camelCase_producer()
    {
        // The naming policy path through ResolveClrName — the one KnownFieldNames actually serves.
        var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var original = WithOptions([new PersistentObjectAttributeOption { Key = "Stolen" }]);

        var json = JsonSerializer.Serialize(original, camel);
        json.Should().Contain("\"options\"", "the write body must emit the field under the policy's name");

        var roundTripped = JsonSerializer.Deserialize<PersistentObject>(json, camel)!;

        roundTripped.Attributes[0].Options.Should().NotBeNull();
        roundTripped.Attributes[0].Options![0].Key.Should().Be("Stolen");
    }

    [Fact]
    public void Absent_options_deserialize_as_null_not_empty()
    {
        // null and empty mean different things: null is "unchanged, use your own source", empty is
        // "there are genuinely no options". Collapsing them would make every refresh response wipe
        // every dropdown that the hook did not touch.
        var roundTripped = JsonSerializer.Deserialize<PersistentObject>(
            JsonSerializer.Serialize(WithOptions(null)))!;

        roundTripped.Attributes[0].Options.Should().BeNull();
    }

    [Fact]
    public void An_empty_option_list_survives_as_empty()
    {
        var roundTripped = JsonSerializer.Deserialize<PersistentObject>(
            JsonSerializer.Serialize(WithOptions([])))!;

        roundTripped.Attributes[0].Options.Should().NotBeNull().And.BeEmpty();
    }
}
