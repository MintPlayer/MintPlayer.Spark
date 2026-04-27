using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Pins the strongly-typed <see cref="SyncAction{T}"/> wrapper used at owner-module call
/// sites and its <see cref="SyncAction{T}.ToTransport"/> conversion to the dictionary-shaped
/// <see cref="SyncAction"/> sent over HTTP / persisted in RavenDB. The reflection-based
/// EntityToDictionary is what hands sync data to consumers — a regression silently drops
/// fields from cross-module replication.
/// </summary>
public class SyncActionGenericTests
{
    private sealed class Car
    {
        public string? Id { get; set; }
        public string? LicensePlate { get; set; }
        public int Year { get; set; }
    }

    [Fact]
    public void ToTransport_copies_ActionType_Collection_DocumentId_and_Properties()
    {
        var src = new SyncAction<Car>
        {
            ActionType = SyncActionType.Update,
            Collection = "Cars",
            DocumentId = "cars/42",
            Properties = ["LicensePlate", "Year"],
            Data = new Car { Id = "cars/42", LicensePlate = "ABC-123", Year = 2024 },
        };

        var transport = src.ToTransport();

        transport.ActionType.Should().Be(SyncActionType.Update);
        transport.Collection.Should().Be("Cars");
        transport.DocumentId.Should().Be("cars/42");
        transport.Properties.Should().BeEquivalentTo(["LicensePlate", "Year"]);
    }

    [Fact]
    public void ToTransport_serializes_Data_via_reflection_into_a_property_dictionary()
    {
        var src = new SyncAction<Car>
        {
            ActionType = SyncActionType.Insert,
            Collection = "Cars",
            Data = new Car { Id = "cars/1", LicensePlate = "XYZ-999", Year = 2025 },
        };

        var transport = src.ToTransport();

        transport.Data.Should().NotBeNull();
        transport.Data!["Id"].Should().Be("cars/1");
        transport.Data["LicensePlate"].Should().Be("XYZ-999");
        transport.Data["Year"].Should().Be(2025);
    }

    [Fact]
    public void ToTransport_emits_null_Data_when_typed_Data_is_null()
    {
        // Delete actions don't carry payload — the transport's Data must be null, not an empty dict.
        var src = new SyncAction<Car>
        {
            ActionType = SyncActionType.Delete,
            Collection = "Cars",
            DocumentId = "cars/99",
            Data = null,
        };

        var transport = src.ToTransport();

        transport.Data.Should().BeNull();
    }

    [Fact]
    public void ToTransport_serializes_null_property_values_as_null_entries()
    {
        var src = new SyncAction<Car>
        {
            ActionType = SyncActionType.Insert,
            Collection = "Cars",
            Data = new Car { Id = null, LicensePlate = null, Year = 0 },
        };

        var transport = src.ToTransport();

        transport.Data.Should().ContainKey("Id");
        transport.Data!["Id"].Should().BeNull();
        transport.Data["LicensePlate"].Should().BeNull();
        transport.Data["Year"].Should().Be(0);
    }

    [Fact]
    public void ToTransport_uses_property_name_as_dictionary_key_unchanged()
    {
        // Casing matters because consumers project these keys back onto the owner entity.
        var src = new SyncAction<Car>
        {
            ActionType = SyncActionType.Update,
            Collection = "Cars",
            Data = new Car { LicensePlate = "PLT" },
        };

        var transport = src.ToTransport();

        transport.Data!.Keys.Should().BeEquivalentTo(["Id", "LicensePlate", "Year"]);
    }

    [Fact]
    public void ToTransport_omits_Properties_when_source_did_not_set_them()
    {
        var src = new SyncAction<Car>
        {
            ActionType = SyncActionType.Insert,
            Collection = "Cars",
            Data = new Car(),
        };

        var transport = src.ToTransport();

        transport.Properties.Should().BeNull();
    }
}
