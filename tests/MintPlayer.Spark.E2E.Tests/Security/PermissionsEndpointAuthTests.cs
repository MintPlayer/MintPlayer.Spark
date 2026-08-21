using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-1 — GET /spark/permissions/{entityTypeId} is allowed to be called anonymously because
/// the Angular SPA needs to know which program units to render for a visitor. What it MUST
/// NOT do is inflate the flags (<c>canCreate</c>/<c>canEdit</c>/<c>canDelete</c>) beyond
/// what the anonymous group is actually granted in security.json.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class PermissionsEndpointAuthTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public PermissionsEndpointAuthTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unauthenticated_GET_permissions_for_Car_reports_no_access()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        // Car is granted to Administrators + Fleet managers, not the anonymous group. Either the server
        // throws (401/403, also secure) or returns permissions with every flag false.
        try
        {
            var perms = await client.GetPermissionsAsync("Car");
            perms.Should().NotBeNull();
            perms!.CanRead.Should().BeFalse("anonymous should not read Car");
            perms.CanCreate.Should().BeFalse("anonymous should not create Car");
            perms.CanEdit.Should().BeFalse("anonymous should not edit Car");
            perms.CanDelete.Should().BeFalse("anonymous should not delete Car");
        }
        catch (SparkClientException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Fail-closed: also acceptable — anonymous caller simply doesn't reach the endpoint.
        }
    }

    [Fact]
    public async Task Unauthenticated_GET_permissions_for_Company_reports_read_but_no_write()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        // Company has QueryRead/Company granted to anonymous callers — anon can read, but must not
        // see any mutation permissions.
        //
        // CanRead is asserted, not merely implied by the test name. Without it this test passed
        // whether or not the anonymous grant existed at all: it only checked the three write flags,
        // which are false for anonymous regardless. The read grant is the entire subject here, and
        // it is the one thing the assertions did not cover.
        var perms = await client.GetPermissionsAsync("Company");

        perms.Should().NotBeNull();
        perms!.CanRead.Should().BeTrue(
            "security.json grants QueryRead/Company to the anonymous group, and it applies to callers "
            + "who never authenticated");
        perms.CanCreate.Should().BeFalse();
        perms.CanEdit.Should().BeFalse();
        perms.CanDelete.Should().BeFalse();
    }
}
