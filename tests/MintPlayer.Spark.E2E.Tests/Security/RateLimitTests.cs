using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-3 — demo apps must ship a rate limiter. This test hammers an anonymous endpoint and
/// expects at least one 429 response. It intentionally fires sequential requests to keep
/// the assertion deterministic across CI environments.
/// <para>
/// ⚠️ The burst is sized from <see cref="FleetTestHost.RateLimitPermits"/>, <b>not</b> from a
/// literal. The E2E host raises the budget so that 88 serialized tests stop competing for a
/// 150-request window, and a hard-coded burst would then quietly stop reaching the limit — this
/// test would pass by never proving anything, which is the failure mode it exists to prevent.
/// Sizing it from the configured value means changing the budget either keeps this honest or
/// makes it fail loudly.
/// </para>
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
        // Enough to cross the configured budget with margin, whatever that budget is.
        var burst = FleetTestHost.RateLimitPermits + 50;

        var saw429 = false;
        try
        {
            for (var i = 0; i < burst && !saw429; i++)
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
                $"a rapid burst of {burst} anonymous requests to /spark/auth/me should cross the "
                + $"configured limit of {FleetTestHost.RateLimitPermits}");
        }
        finally
        {
            // Fleet's rate limiter is a fixed window partitioned by IP, and every test in this
            // collection shares 127.0.0.1 as its partition key — so leaving the bucket saturated
            // makes the next test inherit our 429s.
            //
            // ⚠️ In a `finally`, which it was not. The drain used to sit after the assertion, so a
            // FAILING run — the one that leaves the bucket most saturated — was exactly the run
            // that skipped the cooldown, converting one failure into a cascade of unrelated ones.
            await Task.Delay(TimeSpan.FromSeconds(11));
        }
    }
}
