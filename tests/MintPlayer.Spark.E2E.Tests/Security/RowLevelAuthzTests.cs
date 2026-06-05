using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-2 / H-3 — entity-type-level grants must NOT imply per-row access. Both the admin and
/// a Fleet-manager user have rights on Car via Fleet's security.json, but the Fleet-manager
/// must not be able to see cars created by the admin (and vice versa). CarActions overrides
/// <c>IsAllowedAsync</c>; DatabaseAccess calls the hook on single load, list load, and query
/// parent-fetch.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class RowLevelAuthzTests
{
    private static readonly Guid GetCarsQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440001");

    private readonly FleetE2ECollectionFixture _fixture;
    public RowLevelAuthzTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private async Task<(SparkClient userBClient, string adminCarId)> SeedTwoUsersAndAdminCarAsync()
    {
        // Seed a second, non-admin account. Fleet managers have QueryReadEditNew/Car in
        // Fleet's security.json — the right tier to prove row-level filtering (entity-type
        // check passes; creator check denies).
        var userBEmail = $"fleet-{Guid.NewGuid():N}@e2e.local";
        var userBPassword = _fixture.Host.AdminPass;
        await _fixture.Host.SeedUserAsync(userBEmail, userBPassword, "Fleet managers");

        // Admin creates a car — CarActions stamps CreatedBy with the admin's id.
        using (var adminClient = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host))
        {
            var created = await adminClient.CreatePersistentObjectAsync(
                CarFixture.New(CarFixture.RandomLicensePlate("RL"), model: "RL1"));
            created.Id.Should().NotBeNullOrEmpty(
                $"admin car create must return id\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

            // Log in as user B in a separate client and return that session.
            var userBClient = SparkClientFactory.ForFleet(_fixture.Host);
            try
            {
                await userBClient.LoginAsync(userBEmail, userBPassword);
                return (userBClient, created.Id!);
            }
            catch
            {
                userBClient.Dispose();
                throw;
            }
        }
    }

    [Fact]
    public async Task User_B_cannot_read_User_As_private_car_by_id()
    {
        var (userBClient, adminCarId) = await SeedTwoUsersAndAdminCarAsync();
        using (userBClient)
        {
            // User B has QueryReadEditNew/Car (entity-type check passes) but is not the
            // creator → row-level filter returns null (surfaced as 404 on the endpoint,
            // surfaced as null PO on the client — both shapes mean "invisible" per M-3).
            var po = await userBClient.GetPersistentObjectAsync(CarFixture.TypeId, adminCarId);
            po.Should().BeNull("user B is not the creator and must not be able to load admin's car by id");
        }
    }

    [Fact]
    public async Task User_B_cannot_list_User_As_private_cars()
    {
        var (userBClient, adminCarId) = await SeedTwoUsersAndAdminCarAsync();
        using (userBClient)
        {
            var cars = await userBClient.ListPersistentObjectsAsync(CarFixture.TypeId);

            cars.Should().NotContain(po => po.Id == adminCarId,
                "admin's car must be absent from user B's list response");
        }
    }

    [Fact]
    public async Task User_B_cannot_execute_child_query_with_User_As_parent_id()
    {
        var (userBClient, adminCarId) = await SeedTwoUsersAndAdminCarAsync();
        using (userBClient)
        {
            // GetCars scoped to admin's car as the parent — the parent fetch must fail the
            // row-level gate and surface as 404 rather than silently run the query unscoped.
            var ex = await Assert.ThrowsAsync<SparkClientException>(
                () => userBClient.ExecuteQueryAsync(GetCarsQueryId, parentId: adminCarId, parentType: CarFixture.TypeName));
            ex.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "parent fetch must apply the row-level gate — cannot scope a query to an inaccessible parent");
        }
    }
}
