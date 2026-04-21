using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-1 — Metadata endpoints must filter their response by the caller's permissions.
/// Each test uses a fresh context with no cookies, so the request reaches the server as an
/// anonymous principal (Everyone group).
///
/// Expected secure behavior: the response includes only entities/queries the caller has
/// at least <c>Query</c> rights on. In Fleet, Everyone has <c>QueryRead/Company</c> — so
/// an anonymous caller sees Company in <c>/spark/types</c> and <c>GetCompanies</c> in
/// <c>/spark/queries</c>, but Car/Person/CarBrand/CarStatus must be filtered out.
/// A blanket 200 + full catalogue is the vulnerability.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class MetadataEndpointAuthTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public MetadataEndpointAuthTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unauthenticated_GET_spark_queries_includes_only_queries_visible_to_anonymous_callers()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/queries");

        response.Status.Should().Be(200);
        var body = await response.TextAsync();

        // Everyone has QueryRead/Company (per App_Data/security.json), so GetCompanies must remain visible.
        body.Should().Contain("GetCompanies",
            "queries the anonymous principal does have QueryRead rights on must stay visible");

        // Everyone does NOT have QueryRead on Car/Person/CarBrand/CarStatus — these must be filtered out.
        body.Should().NotContain("GetCars", "Car query is not granted to Everyone");
        body.Should().NotContain("GetPeople", "Person query is not granted to Everyone");
        body.Should().NotContain("Stolen_Cars", "custom Car query is not granted to Everyone");
    }

    [Fact]
    public async Task Unauthenticated_GET_spark_types_includes_only_types_visible_to_anonymous_callers()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/types");

        response.Status.Should().Be(200);
        var body = await response.TextAsync();

        // Company is Everyone-visible (QueryRead/Company) so its type definition is allowed.
        body.Should().Contain("\"name\":\"Company\"", "Company type is visible to Everyone");

        // Car/Person are not — their schemas must be filtered out entirely.
        body.Should().NotContain("\"name\":\"Car\"", "Car type is not visible to Everyone");
        body.Should().NotContain("\"name\":\"Person\"", "Person type is not visible to Everyone");
    }

    [Fact]
    public async Task Unauthenticated_GET_spark_queries_id_for_protected_query_is_refused()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // The Car "GetCars" query is gated by QueryRead/Car which Everyone does not have.
        // The endpoint must behave as if the query doesn't exist — 404 is the expected shape
        // (M-3 also requires 404 vs 403 to be indistinguishable).
        var response = await page.APIRequest.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/queries/a20e8400-e29b-41d4-a716-446655440001");

        response.Status.Should().Be(404,
            "an anonymous caller has no rights on the Car query and must not receive its definition");
    }

    [Fact]
    public async Task Unauthenticated_GET_spark_aliases_includes_only_aliases_visible_to_anonymous_callers()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/aliases");

        response.Status.Should().Be(200);
        var body = await response.TextAsync();

        // Aliases for protected entities must be filtered out — they'd otherwise map
        // human-readable names to entity/query ids that the caller can't use.
        body.Should().NotContain("\"car\"", "Car alias reveals Car's existence");
        body.Should().NotContain("\"person\"", "Person alias reveals Person's existence");
    }
}
