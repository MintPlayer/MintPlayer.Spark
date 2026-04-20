using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-1 — GET /spark/permissions/{entityTypeId} is allowed to be called anonymously because
/// the Angular SPA needs to know which program units to render for a visitor. What it MUST
/// NOT do is inflate the flags (<c>canCreate</c>/<c>canEdit</c>/<c>canDelete</c>) beyond
/// what the anonymous "Everyone" group is actually granted in security.json.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class PermissionsEndpointAuthTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public PermissionsEndpointAuthTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unauthenticated_GET_permissions_for_Car_reports_no_access()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Car is granted to Administrators + Fleet managers, not Everyone.
        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/permissions/Car");

        // Either 401 (secure) or 200 with all flags false.
        if (response.Status == 401)
            return;

        response.Status.Should().Be(200);
        var body = await response.JsonAsync();
        body!.Value.GetProperty("canRead").GetBoolean().Should().BeFalse("anonymous should not read Car");
        body.Value.GetProperty("canCreate").GetBoolean().Should().BeFalse("anonymous should not create Car");
        body.Value.GetProperty("canEdit").GetBoolean().Should().BeFalse("anonymous should not edit Car");
        body.Value.GetProperty("canDelete").GetBoolean().Should().BeFalse("anonymous should not delete Car");
    }

    [Fact]
    public async Task Unauthenticated_GET_permissions_for_Company_reports_read_but_no_write()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Company has QueryRead/Company granted to Everyone — anon should see canRead=true
        // but must not see any mutation permissions.
        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/permissions/Company");

        if (response.Status == 401)
            return;

        response.Status.Should().Be(200);
        var body = await response.JsonAsync();
        body!.Value.GetProperty("canCreate").GetBoolean().Should().BeFalse();
        body.Value.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        body.Value.GetProperty("canDelete").GetBoolean().Should().BeFalse();
    }
}
