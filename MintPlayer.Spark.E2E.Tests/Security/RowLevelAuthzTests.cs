using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-2 / H-3 — entity-type-level grants must NOT imply per-row access. Both the admin and
/// a Fleet-manager user have rights on Car via Fleet's security.json, but the Fleet-manager
/// must not be able to see cars created by the admin (and vice versa). The Car entity now
/// carries a <c>CreatedBy</c> field, CarActions overrides <c>IsAllowedAsync</c>, and
/// DatabaseAccess calls the hook on single load, list load, and query parent-fetch.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class RowLevelAuthzTests
{
    private const string CarObjectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce";

    private readonly FleetE2ECollectionFixture _fixture;
    public RowLevelAuthzTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private async Task<(SparkApi userBApi, string adminCarId)> SeedTwoUsersAndAdminCarAsync()
    {
        // Seed a second, non-admin account if the fixture hasn't already. Fleet managers
        // have QueryReadEditNew/Car in Fleet's security.json — the right tier for this test.
        var userBEmail = $"fleet-{Guid.NewGuid():N}@e2e.local";
        var userBPassword = _fixture.Host.AdminPass; // reuse the complex password from the fixture
        await _fixture.Host.SeedUserAsync(userBEmail, userBPassword, "Fleet managers");

        // Admin creates a car — CarActions will stamp CreatedBy with the admin's id.
        await using var adminPages = new PageFactory(_fixture);
        var adminPage = await adminPages.NewPageAsync();
        var adminApi = await SparkApi.LoginAsync(adminPage, _fixture.Host,
            _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        var plate = $"RL{Guid.NewGuid():N}".Substring(0, 8).ToUpperInvariant();
        var create = await adminApi.PostJsonAsync("/spark/po/Car", new
        {
            persistentObject = new
            {
                name = "Car",
                objectTypeId = CarObjectTypeId,
                attributes = new object[]
                {
                    new { name = "LicensePlate", value = plate },
                    new { name = "Model", value = "RL1" },
                    new { name = "Year", value = 2024 },
                },
            },
        });
        create.Status.Should().BeOneOf(new[] { 200, 201 },
            $"admin car create failed: {await create.TextAsync()}");
        var adminCarId = (await create.JsonAsync())!.Value.GetProperty("id").GetString()!;

        // Now log in as user B in a fresh context and return that session.
        var userBPages = new PageFactory(_fixture);
        var userBPage = await userBPages.NewPageAsync();
        var userBApi = await SparkApi.LoginAsync(userBPage, _fixture.Host, userBEmail, userBPassword);
        return (userBApi, adminCarId);
    }

    [Fact]
    public async Task User_B_cannot_read_User_As_private_car_by_id()
    {
        var (userBApi, adminCarId) = await SeedTwoUsersAndAdminCarAsync();

        // User B has QueryReadEditNew/Car (Fleet managers group) so the entity-type check
        // passes, but they are not CreatedBy on this car. Row-level filter must return 404.
        var response = await userBApi.GetAsync($"/spark/po/Car/{Uri.EscapeDataString(adminCarId)}");
        response.Status.Should().Be(404,
            "user B is not the creator and must not be able to load admin's car by id");
    }

    [Fact]
    public async Task User_B_cannot_list_User_As_private_cars()
    {
        var (userBApi, adminCarId) = await SeedTwoUsersAndAdminCarAsync();

        var response = await userBApi.GetAsync("/spark/po/Car");
        response.Status.Should().Be(200);
        var body = await response.TextAsync();

        body.Should().NotContain(adminCarId,
            "admin's car id must be absent from user B's list response");
    }

    [Fact]
    public async Task User_B_cannot_execute_child_query_with_User_As_parent_id()
    {
        var (userBApi, adminCarId) = await SeedTwoUsersAndAdminCarAsync();

        // GetCars is the Fleet query over Car. Attempt to execute it scoped to admin's car
        // as the parent — the parent fetch must fail the same row-level gate and return 404,
        // not silently run the query unscoped.
        const string getCarsQueryId = "a20e8400-e29b-41d4-a716-446655440001";
        var response = await userBApi.GetAsync(
            $"/spark/queries/{getCarsQueryId}/execute?parentId={Uri.EscapeDataString(adminCarId)}&parentType=Car");

        response.Status.Should().Be(404,
            "parent fetch must apply the row-level gate — cannot scope a query to an inaccessible parent");
    }
}
