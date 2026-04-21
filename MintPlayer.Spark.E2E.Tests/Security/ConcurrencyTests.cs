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

        var carTypeId = Guid.Parse("facb6829-f2a1-4ae2-a046-6ba506e8c0ce");
        var plate = $"CC{Guid.NewGuid():N}".Substring(0, 8).ToUpperInvariant();

        // Create a car. Id is assigned server-side and returned on the created PO.
        var newCar = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = carTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "LicensePlate", Value = plate },
                new PersistentObjectAttribute { Name = "Model", Value = "CC1" },
                new PersistentObjectAttribute { Name = "Year", Value = 2024 },
            ],
        };
        var created = await client.CreatePersistentObjectAsync(newCar);
        created.Id.Should().NotBeNullOrEmpty(
            $"create must return the server-assigned id\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        // Read v1 and capture its etag — the stale snapshot both clients will be working from.
        var v1 = await client.GetPersistentObjectAsync(carTypeId, created.Id!);
        v1.Should().NotBeNull();
        var etagV1 = v1!.Etag;
        etagV1.Should().NotBeNullOrEmpty("server must surface the change vector as Etag for optimistic concurrency");

        // Client A writes first with the v1 etag → succeeds, server moves to v2.
        SetAttribute(v1, "Year", 2025);
        var v2 = await client.UpdatePersistentObjectAsync(v1);
        v2.Etag.Should().NotBe(etagV1);

        // Client B writes based on the stale v1 snapshot with the v1 etag — 409 expected.
        var stale = new PersistentObject
        {
            Id = created.Id,
            Etag = etagV1,
            Name = "Car",
            ObjectTypeId = carTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "LicensePlate", Value = plate },
                new PersistentObjectAttribute { Name = "Model", Value = "CC1" },
                new PersistentObjectAttribute { Name = "Year", Value = 2026 },
            ],
        };

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => client.UpdatePersistentObjectAsync(stale));
        ex.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the second writer's request is based on a stale version and must be rejected with 409 Conflict");
    }

    private static void SetAttribute(PersistentObject po, string name, object value)
    {
        var attr = po.Attributes.FirstOrDefault(a => a.Name == name)
            ?? throw new InvalidOperationException($"Attribute '{name}' not on PO '{po.Id}'.");
        attr.Value = value;
    }
}
