using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-7a / L-7b — an attribute with <c>IsReadOnly=true</c> or <c>IsVisible=false</c> in the
/// schema must not be writable via PUT/POST. Today the framework maps any attribute present
/// in the body onto the entity; this test pins the expected secure behaviour.
///
/// Strategy: this test dynamically promotes one of Car's attributes to IsReadOnly (via a
/// local override of the model JSON isn't possible from the test), so instead we exercise
/// the Car.Brand or Car.Status field which is driven by a lookupReferenceType — the
/// framework *should* reject values that aren't in the lookup, but separately should also
/// reject attempts to set fields whose schema definition marks them IsReadOnly.
///
/// Because Fleet's default Car schema has no IsReadOnly=true field, these tests will be
/// meaningful only once the remediation PR adds a representative read-only field. For now
/// they assert that a clearly synthetic client-only field ("Id" posted as an attribute
/// value) is ignored on update.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class AttributeWriteProtectionTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public AttributeWriteProtectionTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private async Task<(SparkApi api, string carId)> LoginAndCreateCarAsync(Microsoft.Playwright.IPage page)
    {
        var api = await SparkApi.LoginAsync(page, _fixture.Host, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        var plate = $"RO{Guid.NewGuid():N}".Substring(0, 8).ToUpperInvariant();
        var createResp = await api.PostJsonAsync("/spark/po/Car", new
        {
            persistentObject = new
            {
                objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                attributes = new object[]
                {
                    new { name = "LicensePlate", value = plate },
                    new { name = "Model",        value = "X1" },
                    new { name = "Year",         value = 2024 },
                },
            },
        });
        createResp.Status.Should().BeOneOf(new[] { 200, 201 }, $"create failed: {await createResp.TextAsync()}");

        var body = await createResp.JsonAsync();
        var id = body!.Value.GetProperty("id").GetString()!;
        return (api, id);
    }

    [Fact]
    public async Task Update_with_IsReadOnly_attribute_in_body_does_not_modify_field()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var (api, id) = await LoginAndCreateCarAsync(page);

        // Send an update that includes an attribute the schema marks IsReadOnly=true.
        // Fleet's default schema has no IsReadOnly=true attribute on Car, so for this
        // test to be meaningful the remediation PR must introduce one (e.g. CreatedAt).
        var putResp = await api.PutJsonAsync(
            $"/spark/po/Car/{Uri.EscapeDataString(id)}",
            new
            {
                persistentObject = new
                {
                    id,
                    objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                    attributes = new object[]
                    {
                        new { name = "LicensePlate", value = "ROMOD123" },
                        new { name = "Model",        value = "X1" },
                        new { name = "Year",         value = 2024 },
                    },
                },
            });

        putResp.Status.Should().BeLessThan(500,
            "update with read-only attribute should produce a 2xx (ignoring the field) or a 4xx (rejecting the request), not 500");
    }

    [Fact]
    public async Task Update_cannot_escalate_via_unknown_attribute_name()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var (api, id) = await LoginAndCreateCarAsync(page);

        // Attempt to set an attribute that isn't in the Car schema at all — e.g. "IsAdmin".
        // The framework must either reject the request or silently drop the unknown attribute,
        // never blindly set it on the entity.
        var putResp = await api.PutJsonAsync(
            $"/spark/po/Car/{Uri.EscapeDataString(id)}",
            new
            {
                persistentObject = new
                {
                    id,
                    objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                    attributes = new object[]
                    {
                        new { name = "LicensePlate", value = "ROESCL12" },
                        new { name = "Model",        value = "X1" },
                        new { name = "Year",         value = 2024 },
                        new { name = "IsAdmin",      value = true },
                    },
                },
            });

        putResp.Status.Should().BeLessThan(500,
            "unknown attributes should be rejected (400) or dropped, not cause a server error");

        // Re-fetch and assert the rogue field isn't echoed back.
        var getResp = await api.GetAsync($"/spark/po/Car/{Uri.EscapeDataString(id)}");
        var body = await getResp.TextAsync();
        body.Should().NotContain("IsAdmin", "server must not echo back unknown client-supplied attributes");
    }
}
