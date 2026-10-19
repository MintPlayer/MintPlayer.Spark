using System.Diagnostics;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Commerce.Tests.Inventory;

/// <summary>
/// Spikes <b>S2</b> and <b>S3</b> (commerce PRD §7).
/// <para>
/// S2: successful holds per second and p95 latency for K ∈ {1, 8, 32, 64} shards, plus the chain cursor (one hot
/// compare-exchange key every claim updates). A load spike: it runs only with <c>SPARK_SPIKE_S2=1</c>, in a window the
/// owner chooses (D24), against <c>SPARK_SPIKE_RAVEN_URL</c> (localhost:8080, pinned to 3 cores) when set, otherwise
/// the embedded test server. <c>SPARK_SPIKE_S2_CLAIMANTS</c> (default 1,000) and <c>SPARK_SPIKE_S2_CONCURRENCY</c>
/// (default 64) shape it.
/// </para>
/// <para>S3: lapsed holds show as available within ≤5 s with no sweeper at all, and the sweeper only prunes.</para>
/// </summary>
public class EngineThroughputSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private static bool LoadEnabled => Environment.GetEnvironmentVariable("SPARK_SPIKE_S2") == "1";
    private static int Claimants => int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_S2_CLAIMANTS"), out var n) ? n : 1_000;
    private static int Concurrency => int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_S2_CONCURRENCY"), out var n) ? n : 64;

    [Fact]
    public async Task S2_holds_per_second_by_shard_count()
    {
        if (!LoadEnabled)
        {
            output.WriteLine("S2 is a load spike: set SPARK_SPIKE_S2=1 to run it.");
            return;
        }

        var (store, database, dispose) = OpenTarget();
        try
        {
            foreach (var cursor in new[] { false, true })
                foreach (var k in new[] { 1, 8, 32, 64 })
                {
                    var engine = new InventoryEngine(store, database, new InventoryEngineOptions { MaxAttempts = 200 });
                    var quotaId = $"quotas/s2-{k}-{cursor}";
                    await engine.CreateQuotaAsync(quotaId, Claimants * 2, k);
                    var latencies = new List<double>();
                    int ok = 0, conflicts = 0;
                    using var gate = new SemaphoreSlim(Concurrency);
                    var wall = Stopwatch.StartNew();
                    await Task.WhenAll(Enumerable.Range(0, Claimants).Select(async c =>
                    {
                        await gate.WaitAsync();
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            var result = await engine.TryClaimAsync(new ClaimRequest(
                                $"claims/s2-{k}-{cursor}-{c}", $"baskets/{c}", DateTimeOffset.UtcNow.AddMinutes(10),
                                [new QuotaLine(quotaId, 1)]));
                            if (result.Succeeded && cursor)
                                await AdvanceCursorAsync(store, database, $"inventory/cursors/s2-{k}");
                            sw.Stop();
                            lock (latencies)
                            {
                                latencies.Add(sw.Elapsed.TotalMilliseconds);
                                if (result.Succeeded) ok++; else conflicts++;
                            }
                        }
                        finally { gate.Release(); }
                    }));
                    wall.Stop();
                    latencies.Sort();
                    var p95 = latencies[(int)(latencies.Count * 0.95) - 1];
                    output.WriteLine($"S2 K={k,2} cursor={cursor,-5}: {ok / wall.Elapsed.TotalSeconds,7:F1} holds/s, p50 {latencies[latencies.Count / 2],6:F1} ms, p95 {p95,6:F1} ms, refused {conflicts}, wall {wall.Elapsed.TotalSeconds:F1}s");
                }
        }
        finally { dispose(); }
    }

    /// <summary>The chain cursor: one compare-exchange value every seat assignment in a block group moves.</summary>
    private static async Task AdvanceCursorAsync(IDocumentStore store, string? database, string key)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var session = store.OpenAsyncSession(new SessionOptions { Database = database, TransactionMode = TransactionMode.ClusterWide });
                var value = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<CursorValue>(key);
                if (value is null) session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, new CursorValue { Position = 1 });
                else value.Value.Position++;
                await session.SaveChangesAsync();
                return;
            }
            catch (Raven.Client.Exceptions.ConcurrencyException) when (attempt < 200)
            {
                await Task.Delay(Random.Shared.Next(1, 8));
            }
        }
    }

    private sealed class CursorValue { public long Position { get; set; } }

    private (IDocumentStore Store, string? Database, Action Dispose) OpenTarget()
    {
        var url = Environment.GetEnvironmentVariable("SPARK_SPIKE_RAVEN_URL");
        if (string.IsNullOrEmpty(url)) return (Store, null, () => { });

        var database = $"SparkSpikeS2-{Guid.NewGuid():N}";
        var store = new DocumentStore { Urls = [url], Database = database }.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));
        return (store, database, () =>
        {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true, timeToWaitForConfirmation: TimeSpan.Zero));
            store.Dispose();
        });
    }

    [Fact]
    public async Task S3_lapsed_holds_show_as_available_without_a_sweeper()
    {
        var grace = TimeSpan.FromMilliseconds(500);
        var engine = new InventoryEngine(Store, options: new InventoryEngineOptions { Grace = grace });
        await engine.CreateQuotaAsync("quotas/s3", 10, 4);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(1);
        (await engine.TryClaimAsync(new ClaimRequest("claims/s3-a", "baskets/a", expiresAt, [new QuotaLine("quotas/s3", 10)]))).Succeeded.Should().BeTrue();
        (await engine.TryClaimAsync(new ClaimRequest("claims/s3-b", "baskets/b", expiresAt, [new QuotaLine("quotas/s3", 1)]))).Status.Should().Be(ClaimStatus.SoldOut);

        var sw = Stopwatch.StartNew();
        while ((await engine.GetAvailabilityAsync("quotas/s3")).Available < 10)
        {
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "lapsed holds must show as available within 5 s");
            await Task.Delay(50);
        }
        var shown = DateTimeOffset.UtcNow - expiresAt;
        output.WriteLine($"S3: available again {shown.TotalMilliseconds:F0} ms after expiry (grace {grace.TotalMilliseconds} ms), no sweeper");

        // Correctness without the sweeper: the lapsed hold is taken over by the next claim, and the owner can no longer convert.
        (await engine.TryClaimAsync(new ClaimRequest("claims/s3-c", "baskets/c", DateTimeOffset.UtcNow.AddMinutes(1), [new QuotaLine("quotas/s3", 10)]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/s3-a")).Status.Should().Be(ClaimStatus.Expired);
        (await engine.GetAvailabilityAsync("quotas/s3")).Available.Should().Be(0);

        // The sweeper prunes and changes nothing visible.
        await engine.SweepAsync("quotas/s3");
        (await engine.GetAvailabilityAsync("quotas/s3")).Should().Be(new QuotaAvailability("quotas/s3", 10, 0, 10, 0));
    }
}
