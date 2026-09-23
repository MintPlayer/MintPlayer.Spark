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
            // ⚠️ CONCURRENT, and that is the whole point: the limiter is a FIXED WINDOW, so this test
            // races the very window it is testing. Sent one at a time, 1050 round-trips had to finish
            // inside 10 seconds — about 9.5ms each including connection and deserialization — and a
            // shared CI runner simply cannot. The window then rolls over, the count resets, and no 429
            // ever appears: the test failed in CI while passing locally, and the failure looked like a
            // broken limiter rather than a slow one.
            //
            // The old comment called `+ 50` a margin. It is a margin in REQUESTS, and the binding
            // constraint is TIME — which is why widening it would not have helped.
            const int batchSize = 50;

            for (var sent = 0; sent < burst && !saw429; sent += batchSize)
            {
                var inFlight = Math.Min(batchSize, burst - sent);

                await Task.WhenAll(Enumerable.Range(0, inFlight).Select(async _ =>
                {
                    try
                    {
                        await client.GetCurrentUserAsync();
                    }
                    catch (SparkClientException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Benign race: several tasks may write it, all writing the same value.
                        saw429 = true;
                    }
                }));
            }

            saw429.Should().BeTrue(
                $"a rapid burst of {burst} anonymous requests to /spark/auth/me should cross the "
                + $"configured limit of {FleetTestHost.RateLimitPermits} within the limiter's window");
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
