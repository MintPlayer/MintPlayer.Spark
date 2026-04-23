using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-7 — updates must be protected by optimistic concurrency. Two clients reading the
/// same record, both modifying, both writing: the framework must reject the stale write
/// with a 409 Conflict rather than silently losing one client's change. Driven through
/// <see cref="SparkClient"/> so the test body reads as domain operations instead of raw
/// JSON — a PersistentObject round-trip with an etag round-trip as the concurrency token.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ConcurrencyTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ConcurrencyTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_update_with_stale_version_is_rejected()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var plate = CarFixture.RandomLicensePlate("CC");

        // Create a car. Id is assigned server-side and returned on the created PO.
        var created = await client.CreatePersistentObjectAsync(CarFixture.New(plate, model: "CC1"));
        created.Id.Should().NotBeNullOrEmpty(
            $"create must return the server-assigned id\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        // Read v1 and capture its etag — the stale snapshot both clients will be working from.
        var v1 = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        v1.Should().NotBeNull();
        var etagV1 = v1!.Etag;
        etagV1.Should().NotBeNullOrEmpty("server must surface the change vector as Etag for optimistic concurrency");

        // Client A writes first with the v1 etag → succeeds, server moves to v2.
        v1[CarFixture.AttributeNames.Year].Value = 2025;
        var v2 = await client.UpdatePersistentObjectAsync(v1);
        v2.Etag.Should().NotBe(etagV1);

        // Client B writes based on the stale v1 snapshot with the v1 etag — 409 expected.
        var stale = CarFixture.New(plate, model: "CC1", year: 2026);
        stale.Id = created.Id;
        stale.Etag = etagV1;

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => client.UpdatePersistentObjectAsync(stale));
        ex.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the second writer's request is based on a stale version and must be rejected with 409 Conflict");
    }
}
