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

    /// <summary>
    /// The list path filters rows the caller may not see.
    /// <para>
    /// The assertion is absence, and <c>ListPersistentObjectsAsync</c> reads through an
    /// eventually-consistent RavenDB index — so on its own, <c>NotContain</c> passes whenever the
    /// freshly-created car has simply not been indexed yet, <b>whether or not row-level filtering
    /// works at all</b>. The admin's list is therefore asserted first as a positive control: it
    /// establishes that the row exists and is visible to someone, which is what makes its absence
    /// for user B evidence of filtering rather than evidence of lag. A stale index now fails the
    /// control loudly instead of passing the real assertion quietly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task User_B_cannot_list_User_As_private_cars()
    {
        var (userBClient, adminCarId) = await SeedTwoUsersAndAdminCarAsync();
        using (userBClient)
        {
            using (var adminClient = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host))
            {
                var adminCars = await adminClient.ListPersistentObjectsAsync(CarFixture.TypeId);
                adminCars.Should().Contain(po => po.Id == adminCarId,
                    "the car must be indexed and visible to its creator before its absence for "
                    + $"another user means anything\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");
            }

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

    /// <summary>
    /// #281 — a row rule over a <c>[FromIndex]</c>-projected entity. <c>Car</c> carries both:
    /// <c>CarActions</c> declares a row filter, and <c>Car.json</c> binds the generic query to
    /// <c>Cars_Overview</c>/<c>VCar</c>. The filter cannot compose into a projection query, so the
    /// post-materialization reload is the only gate — and it used to ask RavenDB for <c>object</c>,
    /// which yields a <c>JObject</c> whenever the document's CLR-type metadata does not resolve. The
    /// compiled <c>Expression&lt;Func&lt;Car, bool&gt;&gt;</c> then failed its argument check and the
    /// request 500'd before a single row was judged.
    /// <para>
    /// Both affected paths are asserted: the generic <c>Database.Cars</c> query (QueryExecutor) and
    /// the PO list (DatabaseAccess, which takes its projection from the entity file's
    /// <c>queryType</c>/<c>indexName</c>). The assertion is presence, not absence, and the metadata
    /// helper waits for indexing — so a stale index fails loudly rather than passing vacuously.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_row_ruled_car_is_still_listed_when_its_document_has_no_resolvable_clr_type()
    {
        var email = $"fleet-{Guid.NewGuid():N}@e2e.local";
        var password = _fixture.Host.AdminPass;
        await _fixture.Host.SeedUserAsync(email, password, "Fleet managers");

        using var client = SparkClientFactory.ForFleet(_fixture.Host);
        await client.LoginAsync(email, password);

        var created = await client.CreatePersistentObjectAsync(
            CarFixture.New(CarFixture.RandomLicensePlate("CT"), model: "CT1"));
        created.Id.Should().NotBeNullOrEmpty(
            "the caller must own a car before its visibility means anything"
            + $"\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        await _fixture.Host.SetUnresolvableClrTypeAsync(created.Id!);

        var result = await client.ExecuteQueryAsync(GetCarsQueryId);
        result.Data.Should().Contain(po => po.Id == created.Id,
            "the row filter is written on Car, so the reload must produce a Car regardless of what "
            + $"the stored metadata claims\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        var cars = await client.ListPersistentObjectsAsync(CarFixture.TypeId);
        cars.Should().Contain(po => po.Id == created.Id,
            $"the PO-list path projects too\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");
    }
}
