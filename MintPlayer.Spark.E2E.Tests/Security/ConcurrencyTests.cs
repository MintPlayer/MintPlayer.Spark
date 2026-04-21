using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-7 — updates must be protected by optimistic concurrency. Two clients reading the
/// same record, both modifying, both writing: the framework must reject the stale write
/// with a 409 Conflict rather than silently losing one client's change.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ConcurrencyTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ConcurrencyTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_update_with_stale_version_is_rejected()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var api = await SparkApi.LoginAsync(page, _fixture.Host, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        var plate = $"CC{Guid.NewGuid():N}".Substring(0, 8).ToUpperInvariant();
        var create = await api.PostJsonAsync("/spark/po/Car", new
        {
            persistentObject = new
            {
                name = "Car",
                objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                attributes = new object[]
                {
                    new { name = "LicensePlate", value = plate },
                    new { name = "Model",        value = "CC1" },
                    new { name = "Year",         value = 2024 },
                },
            },
        });
        create.Status.Should().BeOneOf(new[] { 200, 201 },
            $"create failed ({create.Status}): body=[{await create.TextAsync()}]\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");
        var body = await create.JsonAsync();
        var id = body!.Value.GetProperty("id").GetString()!;

        // Read v1 and capture its etag — the stale snapshot both clients will be working from.
        var readA = await api.GetAsync($"/spark/po/Car/{Uri.EscapeDataString(id)}");
        readA.Status.Should().Be(200);
        var etagV1 = (await readA.JsonAsync())!.Value.GetProperty("etag").GetString();
        etagV1.Should().NotBeNullOrEmpty("server must surface the change vector as Etag for optimistic concurrency");

        // Client A writes first with the v1 etag → succeeds, server moves to v2.
        var writeA = await api.PutJsonAsync(
            $"/spark/po/Car/{Uri.EscapeDataString(id)}",
            new
            {
                persistentObject = new
                {
                    id,
                    etag = etagV1,
                    name = "Car",
                    objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                    attributes = new object[]
                    {
                        new { name = "LicensePlate", value = plate },
                        new { name = "Model",        value = "CC1" },
                        new { name = "Year",         value = 2025 },
                    },
                },
            });
        writeA.Status.Should().BeOneOf(new[] { 200, 201 }, $"first write failed: {await writeA.TextAsync()}");

        // Client B writes based on the stale v1 snapshot (Year=2026) — same etagV1 as A used.
        // Server's current change vector is now v2, so the v1 etag no longer matches. 409 expected.
        var writeB = await api.PutJsonAsync(
            $"/spark/po/Car/{Uri.EscapeDataString(id)}",
            new
            {
                persistentObject = new
                {
                    id,
                    etag = etagV1,
                    name = "Car",
                    objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                    attributes = new object[]
                    {
                        new { name = "LicensePlate", value = plate },
                        new { name = "Model",        value = "CC1" },
                        new { name = "Year",         value = 2026 },
                    },
                },
            });

        writeB.Status.Should().Be(409,
            "the second writer's request is based on a stale version and must be rejected with 409 Conflict");
    }
}
