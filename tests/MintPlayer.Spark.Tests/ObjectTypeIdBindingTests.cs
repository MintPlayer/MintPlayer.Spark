using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Clients address an entity type by alias as readily as by id (<c>/po/car/new</c>), and echoing
/// that alias back in the request body is the obvious thing for one to do. Before this binding,
/// doing so threw inside deserialization — so the caller got a bare 500 naming no field, no endpoint
/// code ran, and a server-side hook that was supposed to fire simply never did.
///
/// <para>
/// Tolerating it is safe because no endpoint trusts the field: Create, Update, Refresh and
/// ExecuteCustomAction all resolve the type from the route and overwrite it, deliberately, so a
/// client cannot reach one collection through another's permissions.
/// </para>
/// </summary>
public class ObjectTypeIdBindingTests
{
    private static string Json(string objectTypeId) => $$"""
    { "name": "Car", "objectTypeId": {{objectTypeId}}, "attributes": [] }
    """;

    /// <summary>Matches how the endpoints bind: camelCase on the wire.</summary>
    private static readonly JsonSerializerOptions Wire = new() { PropertyNameCaseInsensitive = true };

    private static PersistentObject Bind(string objectTypeId) =>
        JsonSerializer.Deserialize<PersistentObject>(Json(objectTypeId), Wire)!;

    [Fact]
    public void A_guid_binds_normally()
    {
        var po = Bind("\"facb6829-f2a1-4ae2-a046-6ba506e8c0ce\"");

        po.ObjectTypeId.Should().Be(Guid.Parse("facb6829-f2a1-4ae2-a046-6ba506e8c0ce"));
    }

    [Fact]
    public void An_alias_binds_to_empty_instead_of_throwing()
    {
        // The regression. "car" is what the route segment carries, and it used to take the whole
        // request down before the handler could resolve the real type from that same route.
        var po = Bind("\"car\"");

        po.ObjectTypeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Null_binds_to_empty()
    {
        var po = Bind("null");

        po.ObjectTypeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void A_non_string_binds_to_empty()
    {
        var po = Bind("12345");

        po.ObjectTypeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void The_id_still_round_trips_as_a_guid_string()
    {
        var original = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = Guid.Parse("facb6829-f2a1-4ae2-a046-6ba506e8c0ce"),
            Attributes = [],
        };

        var json = JsonSerializer.Serialize(original);
        json.Should().Contain("facb6829-f2a1-4ae2-a046-6ba506e8c0ce");

        JsonSerializer.Deserialize<PersistentObject>(json)!.ObjectTypeId
            .Should().Be(original.ObjectTypeId);
    }
}
