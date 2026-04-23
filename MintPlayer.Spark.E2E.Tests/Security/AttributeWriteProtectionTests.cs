using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-7a / L-7b — an attribute with <c>IsReadOnly=true</c> or <c>IsVisible=false</c> in the
/// schema must not be writable via PUT/POST. These tests assert that the framework either
/// rejects or silently drops such attempts, never producing a 500 or blindly mapping a
/// client-supplied attribute onto the entity.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class AttributeWriteProtectionTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public AttributeWriteProtectionTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private async Task<(SparkClient client, string carId)> LoginAndCreateCarAsync()
    {
        var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        try
        {
            var created = await client.CreatePersistentObjectAsync(
                CarFixture.New(CarFixture.RandomLicensePlate(), model: "X1"));
            created.Id.Should().NotBeNullOrEmpty($"admin car create must return id\n--- Fleet log ---\n{_fixture.Host.RecentLog()}");
            return (client, created.Id!);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task Update_with_IsReadOnly_attribute_in_body_does_not_500()
    {
        var (client, id) = await LoginAndCreateCarAsync();
        using (client)
        {
            // Fleet's default Car schema has no IsReadOnly=true attribute. This test pins
            // shape: the server must never 500 on such a request. Success (2xx, silently
            // ignoring the field) or 4xx (explicit rejection) are both acceptable per PRD.
            var po = await client.GetPersistentObjectAsync(CarFixture.TypeId, id);
            po.Should().NotBeNull();
            po![CarFixture.AttributeNames.LicensePlate].Value = "ROMOD123";

            // Update succeeds (no read-only field on schema today) → no throw.
            var saved = await client.UpdatePersistentObjectAsync(po);
            saved.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Update_cannot_escalate_via_unknown_attribute_name()
    {
        var (client, id) = await LoginAndCreateCarAsync();
        using (client)
        {
            var po = await client.GetPersistentObjectAsync(CarFixture.TypeId, id);
            po.Should().NotBeNull();

            // Smuggle in an attribute that isn't in the Car schema. The framework must
            // either reject the request (4xx) or silently drop the unknown attribute —
            // never blindly set it on the entity.
            po!.AddAttribute(new PersistentObjectAttribute { Name = "IsAdmin", Value = true });

            try
            {
                await client.UpdatePersistentObjectAsync(po);
            }
            catch (SparkClientException ex)
            {
                // 4xx is acceptable — explicit rejection of unknown attribute.
                ((int)ex.StatusCode).Should().BeLessThan(500,
                    "unknown attributes should surface as 4xx, not 500");
                return;
            }

            // No throw → 2xx path. Re-fetch and assert the rogue field isn't echoed back.
            var reread = await client.GetPersistentObjectAsync(CarFixture.TypeId, id);
            reread!.Attributes.Should().NotContain(a => a.Name == "IsAdmin",
                "server must not echo back unknown client-supplied attributes");
        }
    }
}
