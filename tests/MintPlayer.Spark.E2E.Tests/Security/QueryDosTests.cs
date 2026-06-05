using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-M2 — Execute.cs clamps `take` to [1, 1000]. Previously accepted Int32.MaxValue,
/// materializing entire collections before in-memory paging.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class QueryDosTests
{
    // GetCompanies (Company). Everyone has QueryRead/Company in App_Data/security.json,
    // so anonymous /execute is authorized — the point of this test is clamping, not authz.
    // (…440002 is GetPeople/Person, which Everyone may NOT query — that returned 401.)
    private static readonly Guid GetCompaniesQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440003");

    private readonly FleetE2ECollectionFixture _fixture;
    public QueryDosTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Query_execute_clamps_take_to_maximum_page_size()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        // Request an astronomical take. Server must clamp; this also means the
        // request completes quickly (no full-collection materialization).
        var start = DateTime.UtcNow;
        var response = await http.GetAsync(
            $"/spark/queries/{GetCompaniesQueryId}/execute?take=2147483647");
        var elapsed = DateTime.UtcNow - start;

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the endpoint should clamp rather than return 400 (forward-compat with old clients)");

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "an unbounded take used to materialize the full collection — the clamp keeps this snappy");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Query_execute_normalizes_invalid_skip_take_inputs(int badValue)
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var response = await http.GetAsync(
            $"/spark/queries/{GetCompaniesQueryId}/execute?skip={badValue}&take={badValue}");

        // Negative skip clamps to 0; take in [-1, 0] clamps to 1. Either way the
        // request succeeds (doesn't 500).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "out-of-range skip/take values must be clamped, not 500'd");
    }
}
