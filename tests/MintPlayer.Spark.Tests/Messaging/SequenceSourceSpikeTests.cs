using System.Collections.Concurrent;
using System.Diagnostics;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Operations.Counters;
using Raven.Client.Documents.Session;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Spike <b>S-M7</b> (#460, M16b): where the single feeder's FIFO key <c>Sequence</c> comes from. It
/// must be server-assigned (not the producer's clock), monotonic in commit order, and cheap per enqueue.
/// <para>
/// Each candidate enqueues the same messages from <see cref="Producers"/> sessions in parallel, and is
/// judged against the ground truth: the document etag, which is RavenDB's commit order and the order
/// the subscription delivers in (a fresh single-node database, so the change vector is one
/// <c>A:{etag}-{dbid}</c> entry). An <i>inversion</i> is a message committed after another but given a
/// smaller key. The last part measures how far the sort index lags a 1,000-message bulk load.
/// </para>
/// <para>
/// The kept test asserts what does not depend on timing — the index computes the key it documents,
/// holds exactly the claimable set, and <c>@last-modified</c> never inverts commit order — and prints
/// the figures the PRD records (§4.1, M16b).
/// </para>
/// </summary>
public class SequenceSourceSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private const int Producers = 8;
    /// <summary>Messages per candidate: 100 by default (CI); the PRD figures are <c>SPARK_SPIKE_SM7_N=1000</c>.</summary>
    private static int Total => int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_SM7_N"), out var n) ? n : 100;

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(SparkMessages_ByPriority).Assembly];

    private static SparkMessage NewMessage(string queue) => new()
    {
        QueueName = queue,
        MessageType = "spike",
        PayloadJson = "{}",
        CreatedAtUtc = DateTime.UtcNow,
        Status = EMessageStatus.Pending,
        MaxAttempts = 5,
    };

    private static SparkMessage Sem(Action<SparkMessage> change)
    {
        var message = NewMessage("sm7-sem");
        change(message);
        return message;
    }

    /// <summary>A message as written before M16b: no Priority, QueuedAtUtc, WakeUp or NextAttemptAtUtc member.</summary>
    private sealed class LegacyMessage
    {
        public string? Id { get; set; }
        public string QueueName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private sealed record Enqueued(string Id, long? Key, TimeSpan Took, int Retries = 0);

    [Fact]
    public async Task S_M7_sequence_source_cost_ordering_and_index_lag()
    {
        output.WriteLine($"S-M7 index map: {new SparkMessages_ByPriority().CreateIndexDefinition().Maps.Single()}");

        // (a) @last-modified: no extra work at enqueue; the key is read afterwards.
        var a = await RunAsync("sm7-a", async _ =>
        {
            var sw = Stopwatch.StartNew();
            using var session = Store.OpenAsyncSession();
            var m = NewMessage("sm7-a");
            await session.StoreAsync(m);
            await session.SaveChangesAsync();
            return new Enqueued(m.Id!, null, sw.Elapsed);
        });
        await ReportAsync("(a) @last-modified", a, (etag, lastModified, key) => lastModified.Ticks);

        // (b1) a cluster-wide compare-exchange counter, taken before the store.
        const string counterKey = "sm7/sequence";
        var b1 = await RunAsync("sm7-b1", async _ =>
        {
            var sw = Stopwatch.StartNew();
            var retries = 0;
            long next;
            while (true)
            {
                var current = await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<long>(counterKey));
                next = (current?.Value ?? 0) + 1;
                var put = await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<long>(counterKey, next, current?.Index ?? 0));
                if (put.Successful) break;
                retries++;
            }
            using var session = Store.OpenAsyncSession();
            var m = NewMessage("sm7-b1");
            await session.StoreAsync(m);
            await session.SaveChangesAsync();
            return new Enqueued(m.Id!, next, sw.Elapsed, retries);
        });
        await ReportAsync("(b1) compare-exchange counter", b1, (etag, lastModified, key) => key!.Value);

        // (b2) a RavenDB counter incremented by a CounterBatchOperation, which returns the new total.
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new SparkMessage { QueueName = "sm7-counter-holder", Status = EMessageStatus.Completed }, "sm7/counter-holder");
            await session.SaveChangesAsync();
        }
        var b2 = await RunAsync("sm7-b2", async _ =>
        {
            var sw = Stopwatch.StartNew();
            var detail = await Store.Operations.SendAsync(new CounterBatchOperation(new CounterBatch
            {
                ReplyWithAllNodesValues = false,
                Documents =
                [
                    new DocumentCountersOperation
                    {
                        DocumentId = "sm7/counter-holder",
                        Operations = [new CounterOperation { Type = CounterOperationType.Increment, CounterName = "sequence", Delta = 1 }],
                    },
                ],
            }));
            var next = detail.Counters[0]!.TotalValue;
            using var session = Store.OpenAsyncSession();
            var m = NewMessage("sm7-b2");
            await session.StoreAsync(m);
            await session.SaveChangesAsync();
            return new Enqueued(m.Id!, next, sw.Elapsed);
        });
        await ReportAsync("(b2) document counter", b2, (etag, lastModified, key) => key!.Value);

        // (b3) batch enqueue: one compare-exchange reservation of a range of 100, one session for the 100.
        {
            const int batch = 100;
            const string rangeKey = "sm7/range";
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Total / batch; i++)
            {
                var current = await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<long>(rangeKey));
                await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<long>(rangeKey, (current?.Value ?? 0) + batch, current?.Index ?? 0));
                using var session = Store.OpenAsyncSession();
                for (var j = 0; j < batch; j++)
                    await session.StoreAsync(NewMessage("sm7-b3"));
                await session.SaveChangesAsync();
            }
            output.WriteLine($"S-M7 (b3) batch enqueue with a range reservation: {sw.Elapsed.TotalMilliseconds / Total:F3} ms/message ({Total / batch} batches of {batch})");
        }

        // (c) the first save's etag, written back: a second write per message.
        var c = await RunAsync("sm7-c", async _ =>
        {
            var sw = Stopwatch.StartNew();
            using var session = Store.OpenAsyncSession();
            var m = NewMessage("sm7-c");
            await session.StoreAsync(m);
            await session.SaveChangesAsync();
            var etag = EtagOf(session.Advanced.GetChangeVectorFor(m));
            await Store.Operations.SendAsync(new PatchOperation(m.Id!, null, new PatchRequest
            {
                Script = "this.SequenceProbe = args.s",
                Values = { ["s"] = etag },
            }));
            return new Enqueued(m.Id!, etag, sw.Elapsed);
        });
        await ReportAsync("(c) first-save etag written back", c, (etag, lastModified, key) => key!.Value);

        // (d) a server-side identity id ("SparkMessages|"): assigned by the server on commit.
        var d = await RunAsync("sm7-d", async _ =>
        {
            var sw = Stopwatch.StartNew();
            using var session = Store.OpenAsyncSession();
            var m = NewMessage("sm7-d");
            await session.StoreAsync(m, "SparkMessages|");
            await session.SaveChangesAsync();
            return new Enqueued(m.Id!, long.Parse(m.Id!.Split('/')[^1]), sw.Elapsed);
        });
        await ReportAsync("(d) server identity id", d, (etag, lastModified, key) => key!.Value);

        // ---- the index computes what it documents ----------------------------------------------------
        await IndexSemanticsAsync();

        // ---- index lag under a 1,000-message bulk load ------------------------------------------------
        await IndexLagAsync();
    }

    private async Task<List<Enqueued>> RunAsync(string label, Func<int, Task<Enqueued>> enqueue)
    {
        var results = new ConcurrentBag<Enqueued>();
        var next = -1;
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Producers).Select(_ => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < Total)
                results.Add(await enqueue(i));
        })));
        output.WriteLine($"S-M7 {label}: {Total} messages from {Producers} producers in {wall.Elapsed.TotalMilliseconds:F0} ms");
        return [.. results];
    }

    private async Task ReportAsync(string label, List<Enqueued> enqueued, Func<long, DateTime, long?, long> keyOf)
    {
        var truth = await GroundTruthAsync(enqueued.Select(e => e.Id));
        var byId = enqueued.ToDictionary(e => e.Id);
        var ordered = truth.OrderBy(t => t.Etag).Select(t => keyOf(t.Etag, t.LastModified, byId[t.Id].Key)).ToList();
        int inversions = 0, ties = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i] < ordered[i - 1]) inversions++;
            else if (ordered[i] == ordered[i - 1]) ties++;
        }

        var took = enqueued.Select(e => e.Took.TotalMilliseconds).Order().ToList();
        output.WriteLine($"S-M7 {label}: per enqueue p50={took[took.Count / 2]:F2} ms p95={took[(int)(took.Count * 0.95)]:F2} ms mean={took.Average():F2} ms; "
            + $"inversions vs commit order={inversions}, ties={ties}, retries={enqueued.Sum(e => e.Retries)}");

        if (label.StartsWith("(a)", StringComparison.Ordinal))
            inversions.Should().Be(0, "@last-modified is set by the server when it commits, in commit order");
    }

    private sealed record Truth(string Id, long Etag, DateTime LastModified);

    private async Task<List<Truth>> GroundTruthAsync(IEnumerable<string> ids)
    {
        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<SparkMessage>(ids);
        return loaded.Values.Select(m => new Truth(
            m.Id!,
            EtagOf(session.Advanced.GetChangeVectorFor(m)),
            LastModifiedOf(session.Advanced.GetMetadataFor(m)))).ToList();
    }

    private static DateTime LastModifiedOf(IMetadataDictionary metadata)
        => DateTime.Parse(metadata.GetString("@last-modified"), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>The etag of a single-node change vector, <c>A:{etag}-{dbid}</c>.</summary>
    private static long EtagOf(string changeVector)
    {
        var entry = changeVector.Split(',')[0].Trim();
        return long.Parse(entry[(entry.IndexOf(':') + 1)..entry.IndexOf('-')]);
    }

    private async Task IndexSemanticsAsync()
    {
        var captured = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(Sem(m => { m.Priority = 1; }), "sem/high");
            await session.StoreAsync(Sem(m => { m.Priority = -1; }), "sem/low");
            await session.StoreAsync(Sem(m => { m.QueuedAtUtc = captured; }), "sem/captured");
            await session.StoreAsync(Sem(m => { m.NextAttemptAtUtc = DateTime.UtcNow.AddHours(1); }), "sem/deferred");
            await session.StoreAsync(Sem(m => { m.NextAttemptAtUtc = DateTime.UtcNow.AddHours(-1); m.WakeUp = true; }), "sem/woken");
            await session.StoreAsync(Sem(m => { m.Status = EMessageStatus.Failed; m.WakeUp = true; }), "sem/failed-woken");
            await session.StoreAsync(Sem(m => { m.Status = EMessageStatus.Failed; }), "sem/failed-parked");
            await session.StoreAsync(Sem(m => { m.Status = EMessageStatus.Processing; }), "sem/processing");
            await session.StoreAsync(Sem(m => { m.Status = EMessageStatus.Completed; }), "sem/completed");
            await session.SaveChangesAsync();
        }

        // A message written before M16b: no Priority, no QueuedAtUtc, no WakeUp member at all.
        using (var session = Store.OpenAsyncSession())
        {
            var legacy = new LegacyMessage { QueueName = "sm7-sem", Status = "Pending" };
            await session.StoreAsync(legacy, "sem/legacy");
            session.Advanced.GetMetadataFor(legacy)["@collection"] = "SparkMessages";
            await session.SaveChangesAsync();
        }

        await WaitForIndexesAsync();
        using (var session = Store.OpenAsyncSession())
        {
            var page = await session.Query<SparkMessages_ByPriority.Entry, SparkMessages_ByPriority>()
                .Where(e => e.QueueName == "sm7-sem")
                .OrderByDescending(e => e.Priority)
                .ThenBy(e => e.Sequence)
                .ProjectInto<SparkMessages_ByPriority.Entry>()
                .ToListAsync();
            output.WriteLine($"S-M7 index page: {string.Join(" | ", page.Select(e => $"{e.Id} p={e.Priority} seq={e.Sequence:O}"))}");

            page.Select(e => e.Id).Should().BeEquivalentTo(["sem/high", "sem/low", "sem/captured", "sem/woken", "sem/failed-woken", "sem/legacy"]);
            page[0].Id.Should().Be("sem/high");
            page[^1].Id.Should().Be("sem/low");
            page.Single(e => e.Id == "sem/captured").Sequence.Should().Be(captured, "a captured QueuedAtUtc wins over @last-modified");
            var woken = await session.LoadAsync<SparkMessage>("sem/woken");
            page.Single(e => e.Id == "sem/woken").Sequence.Should().Be(LastModifiedOf(session.Advanced.GetMetadataFor(woken)), "uncaptured, the key is the server's @last-modified");
            page.Single(e => e.Id == "sem/legacy").Priority.Should().Be(0, "an absent Priority reads as Normal");
        }
    }

    private async Task IndexLagAsync()
    {
        await WaitForIndexesAsync();
        const string queue = "sm7-bulk";
        var saved = new ConcurrentDictionary<string, DateTime>();
        var polls = new List<(DateTime At, int Count)>();
        using var stop = new CancellationTokenSource();

        var poller = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var session = Store.OpenAsyncSession();
                var count = await session.Query<SparkMessages_ByPriority.Entry, SparkMessages_ByPriority>()
                    .Customize(x => x.NoCaching())
                    .Where(e => e.QueueName == queue)
                    .CountAsync();
                lock (polls) polls.Add((DateTime.UtcNow, count));
                if (count >= Total) break;
                try { await Task.Delay(2, stop.Token); } catch (OperationCanceledException) { }
            }
        });

        var loadStarted = DateTime.UtcNow;
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, Producers).Select(_ => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) < Total)
            {
                using var session = Store.OpenAsyncSession();
                var m = NewMessage(queue);
                await session.StoreAsync(m);
                await session.SaveChangesAsync();
                saved[m.Id!] = DateTime.UtcNow;
            }
        })));
        var loadTook = DateTime.UtcNow - loadStarted;

        // A failure bound for the poller, never a timing assertion.
        var finished = await Task.WhenAny(poller, Task.Delay(TimeSpan.FromSeconds(60)));
        await stop.CancelAsync();
        finished.Should().Be(poller, "every message becomes visible in the sort index");

        var truth = (await GroundTruthAsync(saved.Keys)).OrderBy(t => t.Etag).ToList();
        var lags = new List<double>();
        var p = 0;
        for (var rank = 1; rank <= truth.Count; rank++)
        {
            while (polls[p].Count < rank) p++;
            lags.Add(Math.Max(0, (polls[p].At - saved[truth[rank - 1].Id]).TotalMilliseconds));
        }
        lags.Sort();
        output.WriteLine($"S-M7 index lag, {Total} messages from {Producers} producers in {loadTook.TotalMilliseconds:F0} ms: "
            + $"p50={lags[lags.Count / 2]:F0} ms p95={lags[(int)(lags.Count * 0.95)]:F0} ms max={lags[^1]:F0} ms "
            + $"(poll granularity ~{(polls[^1].At - polls[0].At).TotalMilliseconds / polls.Count:F1} ms, {polls.Count} polls); "
            + $"last write -> all visible {(polls[^1].At - saved.Values.Max()).TotalMilliseconds:F0} ms");
    }
}
