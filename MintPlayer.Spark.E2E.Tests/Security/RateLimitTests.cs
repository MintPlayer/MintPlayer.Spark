using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-3 — demo apps must ship a rate limiter. This test hammers an anonymous endpoint and
/// expects at least one 429 response. It intentionally fires sequential requests to keep
/// the assertion deterministic across CI environments; the limiter's threshold should be
/// low enough that a 200-request burst crosses it.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class RateLimitTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public RateLimitTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Rapid_unauthenticated_bursts_trigger_429_Too_Many_Requests()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        // GetCurrentUserAsync hits /spark/auth/me, same target as the original test. On 429,
        // the client throws SparkClientException; we break as soon as we see one.
        var saw429 = false;
        for (var i = 0; i < 200 && !saw429; i++)
        {
            try
            {
                await client.GetCurrentUserAsync();
            }
            catch (SparkClientException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                saw429 = true;
            }
        }

        saw429.Should().BeTrue(
            "a rapid burst of 200 anonymous requests to /spark/auth/me should hit the rate limiter");

        // Fleet's rate limiter is a fixed window partitioned by IP. Every test in this
        // collection shares 127.0.0.1 as its partition key, so leaving the bucket saturated
        // would cause the next test to inherit our 429 state. Wait slightly longer than the
        // configured 10 s window so the bucket rolls over before the next test runs.
        await Task.Delay(TimeSpan.FromSeconds(11));
    }
}
