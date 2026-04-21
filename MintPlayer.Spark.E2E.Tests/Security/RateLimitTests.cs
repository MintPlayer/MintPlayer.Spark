using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-3 — demo apps must ship a rate limiter. This test hammers an anonymous endpoint and
/// expects at least one 429 response. It intentionally fires sequential requests to keep
/// the assertion deterministic across CI environments; the limiter's threshold should be
/// low enough that a 50-request burst crosses it.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class RateLimitTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public RateLimitTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Rapid_unauthenticated_bursts_trigger_429_Too_Many_Requests()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var observedStatuses = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/me");
            observedStatuses.Add(response.Status);
            if (observedStatuses.Contains(429))
                break;
        }

        observedStatuses.Should().Contain(429,
            "a rapid burst of 200 anonymous requests to /spark/auth/me should hit the rate limiter");

        // Fleet's rate limiter is a fixed window partitioned by IP. Every test in this
        // collection shares 127.0.0.1 as its partition key, so leaving the bucket saturated
        // would cause the next test to inherit our 429 state (PermissionsEndpointAuth was
        // the first casualty). Wait slightly longer than the configured 10 s window so the
        // bucket rolls over before the next test runs.
        await Task.Delay(TimeSpan.FromSeconds(11));
    }
}
