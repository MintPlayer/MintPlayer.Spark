using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-H8 — EntityMapper now consults the schema's IsReadOnly / IsVisible flags on
/// writes. CarFixture's CreatedBy is IsReadOnly=true + IsVisible=false in Fleet's
/// model JSON. A client posting the field on PUT used to overwrite it; now the
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

        // Read it back to see the actual server-stamped CreatedBy.
        var fresh = await admin.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        fresh.Should().NotBeNull();
        var originalCreatedBy = fresh!.Attributes
            .FirstOrDefault(a => a.Name == "CreatedBy")?.Value?.ToString();

        // Client tries to rewrite CreatedBy via PUT.
        var attemptedCreatedBy = "users/spoofed-id";
        var createdByAttr = fresh.Attributes.First(a => a.Name == "CreatedBy");
        createdByAttr.Value = attemptedCreatedBy;
        createdByAttr.IsValueChanged = true;
        createdByAttr.IsReadOnly = false; // Client lies about readonly state too.

        await admin.UpdatePersistentObjectAsync(fresh);

        // Reload and confirm CreatedBy is unchanged.
        var reloaded = await admin.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        var reloadedCreatedBy = reloaded!.Attributes
            .FirstOrDefault(a => a.Name == "CreatedBy")?.Value?.ToString();

        reloadedCreatedBy.Should().Be(originalCreatedBy,
            "client-supplied write to IsReadOnly=true attribute must be ignored by the entity mapper");
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
        victimReloaded!.Attributes.First(a => a.Name == "Model").Value?.ToString()
            .Should().Be(originalModel,
                "victim record's Model must NOT have been overwritten by the POST");
    }
}
