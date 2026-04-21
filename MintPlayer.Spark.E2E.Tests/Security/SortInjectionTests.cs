using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-5 — the <c>sortColumns</c> query-string parameter must be validated against the
/// query's declared attribute schema. Accepting an arbitrary property name via reflection
/// lets a caller sort on fields the developer never intended to expose (leaking ordering
/// as a side channel, and in some index configurations materialising the value).
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class SortInjectionTests
{
    private static readonly Guid CarsQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440001");

    private readonly FleetE2ECollectionFixture _fixture;
    public SortInjectionTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Sort_by_nonexistent_property_is_rejected()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.ExecuteQueryAsync(CarsQueryId, sortColumns: "NoSuchProperty:asc"));

        ((int)ex.StatusCode).Should().BeOneOf(new[] { 400, 422 },
            "sorting by a property not in the query's schema must be rejected, not silently ignored");
    }

    [Fact]
    public async Task Sort_by_metadata_property_on_projection_is_rejected()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        // The VCar projection is a C# class; any public property on it is reflectable.
        // "Id" is reflectable-but-not-declared on the query — it must be rejected.
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.ExecuteQueryAsync(CarsQueryId, sortColumns: "Id:asc"));

        ((int)ex.StatusCode).Should().BeOneOf(new[] { 400, 422 },
            "sorting by a reflectable-but-undeclared property (Id) must be rejected");
    }

    [Fact]
    public async Task Sort_with_malformed_direction_does_not_500()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        // The request may succeed (server tolerates the garbage direction) or fail with 4xx;
        // the test's point is that it must never be a 500.
        try
        {
            await client.ExecuteQueryAsync(CarsQueryId, sortColumns: "LicensePlate:not-a-direction");
        }
        catch (SparkClientException ex)
        {
            ((int)ex.StatusCode).Should().BeLessThan(500,
                "malformed direction should produce a 4xx client error, not a 500 server fault");
        }
    }
}
