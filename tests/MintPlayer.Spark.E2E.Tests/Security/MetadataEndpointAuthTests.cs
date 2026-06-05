using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-1 — Metadata endpoints must filter their response by the caller's permissions. Each test
/// runs as an anonymous caller (fresh SparkClient with no cookies), so the request reaches
/// the server as the Everyone principal. The response must include only entities/queries the
/// caller has at least <c>Query</c> rights on.
///
/// In Fleet, Everyone has <c>QueryRead/Company</c> — so the anonymous caller sees Company in
/// <see cref="SparkClient.ListEntityTypesAsync"/> and <c>GetCompanies</c> in
/// <see cref="SparkClient.ListQueriesAsync"/>, but Car/Person/CarBrand/CarStatus must be
/// filtered out. A blanket 200 + full catalogue is the vulnerability.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class MetadataEndpointAuthTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public MetadataEndpointAuthTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unauthenticated_list_queries_includes_only_queries_visible_to_anonymous_callers()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        var queries = await client.ListQueriesAsync();
        var names = queries.Select(q => q.Name).ToArray();

        // Everyone has QueryRead/Company per App_Data/security.json, so GetCompanies stays.
        names.Should().Contain("GetCompanies",
            "queries the anonymous principal does have QueryRead rights on must stay visible");

        // Everyone does NOT have QueryRead on Car/Person/CarBrand/CarStatus — filtered out.
        names.Should().NotContain("GetCars", "Car query is not granted to Everyone");
        names.Should().NotContain("GetPeople", "Person query is not granted to Everyone");
        names.Should().NotContain("Stolen_Cars", "custom Car query is not granted to Everyone");
    }

    [Fact]
    public async Task Unauthenticated_list_entity_types_includes_only_types_visible_to_anonymous_callers()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        var types = await client.ListEntityTypesAsync();
        var names = types.Select(t => t.Name).ToArray();

        names.Should().Contain("Company", "Company type is visible to Everyone");
        names.Should().NotContain("Car", "Car type is not visible to Everyone");
        names.Should().NotContain("Person", "Person type is not visible to Everyone");
    }

    [Fact]
    public async Task Unauthenticated_GET_query_by_id_for_protected_query_returns_null()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        // The Car "GetCars" query is gated by QueryRead/Car which Everyone does not have.
        // The endpoint must behave as if the query doesn't exist (M-3: 404 vs 403 must be
        // indistinguishable) — SparkClient surfaces that as null.
        var query = await client.GetQueryAsync(Guid.Parse("a20e8400-e29b-41d4-a716-446655440001"));

        query.Should().BeNull(
            "an anonymous caller has no rights on the Car query and must not receive its definition");
    }

    [Fact]
    public async Task Unauthenticated_list_aliases_includes_only_aliases_visible_to_anonymous_callers()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        var aliases = await client.ListAliasesAsync();

        // Aliases for protected entities must be filtered out — they'd otherwise map
        // human-readable names to entity/query ids that the caller can't use.
        aliases.EntityTypes.Values.Should().NotContain("car", "Car alias reveals Car's existence");
        aliases.EntityTypes.Values.Should().NotContain("person", "Person alias reveals Person's existence");
    }
}
