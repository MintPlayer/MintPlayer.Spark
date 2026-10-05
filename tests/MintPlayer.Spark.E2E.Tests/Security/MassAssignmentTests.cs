using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-H8 — EntityMapper now consults the schema's IsReadOnly flag on writes
/// (visibility stopped being a write gate in #264). CarFixture's CreatedBy is IsReadOnly=true
/// in Fleet's model JSON. A client posting the field on PUT used to overwrite it; now the
/// gate refuses the write.
///
/// R2-M18 — Create endpoint forces obj.Id = null after deserialization, so a POST
/// body with {"Id":"cars/existing"} can no longer flip the action from "New" to
/// "Edit" and overwrite a foreign record under the POST verb.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class MassAssignmentTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public MassAssignmentTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PUT_does_not_modify_isreadonly_attribute()
    {
        using var admin = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        // Admin creates a car — CreatedBy is stamped server-side.
        var created = await admin.CreatePersistentObjectAsync(
            CarFixture.New(CarFixture.RandomLicensePlate("RO"), model: "RO1"));
        created.Id.Should().NotBeNullOrEmpty();

        // Since #271 Car is IAuditCreated: History stamps CreatedBy, and the generated member is
        // [ReadOnly(true)], so the model declares it read-only and it ships to the client as such.
        var originalCreatedBy = (await _fixture.Host.LoadAsync<StoredCar>(created.Id!))?.CreatedBy;
        originalCreatedBy.Should().NotBeNullOrEmpty("the server stamps CreatedBy on create");

        var fresh = await admin.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        fresh.Should().NotBeNull();
        var createdBy = fresh!.Attributes.FirstOrDefault(a => a.Name == "CreatedBy");
        createdBy.Should().NotBeNull("the generated audit member is a model attribute");
        createdBy!.IsReadOnly.Should().BeTrue("the framework stamps it, so the model declares it read-only");

        // Client forges it anyway — changed, and claiming to be writable.
        var attemptedCreatedBy = "users/spoofed-id";
        createdBy.Value = attemptedCreatedBy;
        createdBy.IsValueChanged = true;
        createdBy.IsReadOnly = false;

        await admin.UpdatePersistentObjectAsync(fresh);

        // The mapper consults the model's IsReadOnly, not the client's, so the forged value is dropped.
        var reloadedCreatedBy = (await _fixture.Host.LoadAsync<StoredCar>(created.Id!))?.CreatedBy;
        reloadedCreatedBy.Should().Be(originalCreatedBy,
            "a client-supplied value for a read-only attribute must be ignored by the entity mapper");
        reloadedCreatedBy.Should().NotBe(attemptedCreatedBy,
            "attacker's attempted value must NOT have landed in storage");
    }

    [Fact]
    public async Task POST_with_client_supplied_Id_does_not_overwrite_existing_record()
    {
        using var admin = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        // First, create a victim record we can try to overwrite.
        var victim = await admin.CreatePersistentObjectAsync(
            CarFixture.New(CarFixture.RandomLicensePlate("V"), model: "VICTIM"));
        var victimId = victim.Id!;

        // Read victim's model to confirm it later.
        var fresh = await admin.GetPersistentObjectAsync(CarFixture.TypeId, victimId);
        var originalModel = fresh!.Attributes.First(a => a.Name == "Model").Value?.ToString();

        // POST a new car but try to spoof Id = victim's id.
        var spoof = CarFixture.New(CarFixture.RandomLicensePlate("S"), model: "SPOOFED");
        spoof.Id = victimId;

        var created = await admin.CreatePersistentObjectAsync(spoof);

        // The server must have generated a fresh ID — NOT victim's id.
        created.Id.Should().NotBe(victimId,
            "Create endpoint must force Id=null and let the server generate a fresh id");

        // Victim record must be unchanged.
        var victimReloaded = await admin.GetPersistentObjectAsync(CarFixture.TypeId, victimId);
        (victimReloaded!.Attributes.First(a => a.Name == "Model").Value?.ToString())
            .Should().Be(originalModel,
                "victim record's Model must NOT have been overwritten by the POST");
    }

    /// <summary>The stored shape this test reads; Fleet's entity assembly is not referenced by the E2E project.</summary>
    private sealed class StoredCar
    {
        public string? CreatedBy { get; set; }
    }
}
