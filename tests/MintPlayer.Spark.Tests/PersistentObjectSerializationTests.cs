using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests;

public class PersistentObjectSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesShape_AndReattachesParent()
    {
        var original = new PersistentObject
        {
            Id = "cars/1",
            Name = "Car",
            ObjectTypeId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Breadcrumb = "Alice's car",
            Etag = "etag-123",
            Attributes =
            [
                new PersistentObjectAttribute
                {
                    Name = "LicensePlate",
                    Value = "ABC-123",
                    DataType = "String",
                    IsRequired = true,
                },
                new PersistentObjectAttribute
                {
                    Name = "Year",
                    Value = 2024,
                    DataType = "Int",
                },
            ],
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<PersistentObject>(json)!;

        roundTripped.Id.Should().Be("cars/1");
        roundTripped.Name.Should().Be("Car");
        roundTripped.ObjectTypeId.Should().Be(original.ObjectTypeId);
        roundTripped.Breadcrumb.Should().Be("Alice's car");
        roundTripped.Etag.Should().Be("etag-123");
        roundTripped.Attributes.Should().HaveCount(2);
        roundTripped.Attributes[0].Name.Should().Be("LicensePlate");
        roundTripped.Attributes[0].Value?.ToString().Should().Be("ABC-123");
        roundTripped.Attributes[1].Name.Should().Be("Year");

        // Every deserialized attribute must have its Parent back-reference set.
        roundTripped.Attributes.Should().OnlyContain(a => a.Parent == roundTripped);
    }

    [Fact]
    public void Serialize_DoesNotEmitParent()
    {
        var po = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [new PersistentObjectAttribute { Name = "Plate", Value = "ABC" }],
        };

        var json = JsonSerializer.Serialize(po);

        json.Should().NotContain("\"Parent\"", "cycle + wire contract stability");
        json.Should().NotContain("\"parent\"");
    }

    [Fact]
    public void Deserialize_EmptyAttributes_YieldsEmptyReadOnlyList()
    {
        var json = """
        {
          "Name": "Car",
          "ObjectTypeId": "11111111-2222-3333-4444-555555555555",
          "Attributes": []
        }
        """;

        var po = JsonSerializer.Deserialize<PersistentObject>(json)!;

        po.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_MissingAttributes_YieldsEmptyReadOnlyList()
    {
        var json = """
        {
          "Name": "Car",
          "ObjectTypeId": "11111111-2222-3333-4444-555555555555"
        }
        """;

        var po = JsonSerializer.Deserialize<PersistentObject>(json)!;

        po.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void WireFormat_MatchesDocumentedShape()
    {
        var po = new PersistentObject
        {
            Id = "cars/1",
            Name = "Car",
            ObjectTypeId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Plate", Value = "ABC" },
            ],
        };

        var json = JsonSerializer.Serialize(po);
        var parsed = JsonDocument.Parse(json).RootElement;

        parsed.TryGetProperty("Id", out _).Should().BeTrue();
        parsed.TryGetProperty("Name", out _).Should().BeTrue();
        parsed.TryGetProperty("ObjectTypeId", out _).Should().BeTrue();
        parsed.TryGetProperty("Attributes", out var attrs).Should().BeTrue();
        attrs.ValueKind.Should().Be(JsonValueKind.Array);
        attrs.GetArrayLength().Should().Be(1);
    }
}
