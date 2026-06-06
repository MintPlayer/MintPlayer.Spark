using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Mapper;

/// <summary>
/// PRD §1 (inverse mapping) — exercise the Reference round-trip end-to-end through the
/// Fleet API. <c>Car</c> has a <c>Manager</c> reference to <c>Person</c>; the inverse path
/// must write the refId onto the entity and the forward path must round-trip a breadcrumb
/// resolved from the referenced document on re-fetch.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ReferenceRoundTripTests
{
    private static readonly Guid PersonTypeId = Guid.Parse(PersistentObjectIds.Default.Person);

    private readonly FleetE2ECollectionFixture _fixture;
    public ReferenceRoundTripTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Car_Manager_reference_roundtrips_refid_and_breadcrumb()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var alice = await CreatePersonAsync(client, "Alice", "Adams");
        var bob = await CreatePersonAsync(client, "Bob", "Baker");

        // Create Car with Manager → Alice.
        var plate = CarFixture.RandomLicensePlate("RR");
        var created = await client.CreatePersistentObjectAsync(NewCarWithManager(plate, alice.Id!));
        created.Id.Should().NotBeNullOrEmpty(
            $"car create must return id\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        // Re-fetch: refId flowed through inverse path; forward path resolves the breadcrumb
        // via the [Reference] includes pipeline.
        var refetched = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Created car {created.Id} not re-fetchable");
        var manager = refetched.Attributes.Single(a => a.Name == "Manager");
        manager.Value?.ToString().Should().Be(alice.Id, "inverse path must persist the refId onto the entity");
        manager.Breadcrumb.Should().Be("Alice", "forward path must resolve the Person's display value");

        // Update Manager to Bob. Ensure Attributes collection is mutated through a fresh
        // initializer (PersistentObject.Attributes is init-only, so we rebuild).
        var update = CarWithManagerFromExisting(refetched, bob.Id!);
        await client.UpdatePersistentObjectAsync(update);

        var afterUpdate = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Updated car {created.Id} not re-fetchable");
        var managerAfter = afterUpdate.Attributes.Single(a => a.Name == "Manager");
        managerAfter.Value?.ToString().Should().Be(bob.Id, "update path must overwrite the refId");
        managerAfter.Breadcrumb.Should().Be("Bob", "breadcrumb must reflect the new reference");
    }

    [Fact]
    public async Task Clearing_reference_removes_breadcrumb()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var alice = await CreatePersonAsync(client, "Alice", "Adams");
        var plate = CarFixture.RandomLicensePlate("RC");
        var created = await client.CreatePersistentObjectAsync(NewCarWithManager(plate, alice.Id!));

        // Re-fetch, clear Manager, push update.
        var refetched = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Created car {created.Id} not re-fetchable");
        var cleared = CarWithManagerFromExisting(refetched, managerId: null);
        await client.UpdatePersistentObjectAsync(cleared);

        var afterClear = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Cleared car {created.Id} not re-fetchable");
        var managerAfter = afterClear.Attributes.Single(a => a.Name == "Manager");
        managerAfter.Value?.ToString().Should().BeNullOrEmpty("null/empty incoming refId unsets the reference");
        managerAfter.Breadcrumb.Should().BeNull("cleared reference has no breadcrumb to resolve");
    }

    private static async Task<PersistentObject> CreatePersonAsync(SparkClient client, string firstName, string lastName)
    {
        var po = new PersistentObject
        {
            Name = "Person",
            ObjectTypeId = PersonTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "FirstName", Value = firstName },
                new PersistentObjectAttribute { Name = "LastName", Value = lastName },
            ],
        };
        var created = await client.CreatePersistentObjectAsync(po);
        created.Id.Should().NotBeNullOrEmpty($"Person({firstName} {lastName}) create must return id");
        return created;
    }

    private static PersistentObject NewCarWithManager(string plate, string managerId)
        => new()
        {
            Name = CarFixture.TypeName,
            ObjectTypeId = CarFixture.TypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = CarFixture.AttributeNames.LicensePlate, Value = plate },
                new PersistentObjectAttribute { Name = CarFixture.AttributeNames.Model,        Value = "RR1" },
                new PersistentObjectAttribute { Name = CarFixture.AttributeNames.Year,         Value = 2024 },
                new PersistentObjectAttribute { Name = "Manager", Value = managerId, DataType = "Reference" },
            ],
        };

    /// <summary>
    /// Rebuilds the PO's attribute collection with Manager set to <paramref name="managerId"/>,
    /// preserving every other attribute that came off the wire. Needed because
    /// <see cref="PersistentObject.Attributes"/> is init-only — we can't mutate the existing
    /// collection in place.
    /// </summary>
    private static PersistentObject CarWithManagerFromExisting(PersistentObject existing, string? managerId)
    {
        var attrs = existing.Attributes
            .Where(a => a.Name != "Manager")
            .Select(a => new PersistentObjectAttribute
            {
                Name = a.Name,
                Value = a.Value,
                DataType = a.DataType,
                IsValueChanged = a.IsValueChanged,
            })
            .Append(new PersistentObjectAttribute
            {
                Name = "Manager",
                Value = managerId,
                DataType = "Reference",
                IsValueChanged = true,
            })
            .ToArray();

        return new PersistentObject
        {
            Id = existing.Id,
            Name = existing.Name,
            ObjectTypeId = existing.ObjectTypeId,
            Etag = existing.Etag,
            Attributes = attrs,
        };
    }
}
