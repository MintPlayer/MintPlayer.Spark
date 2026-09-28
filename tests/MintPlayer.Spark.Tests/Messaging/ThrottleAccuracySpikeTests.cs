using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Spike <b>S-M3</b> (#460): throttle accuracy through the real single-subscription pipeline — feeder,
/// lanes, admission, sweeper — on RavenDB, with transactional traffic on another queue alongside.
/// <para>
/// Time is compressed ×20 so the run fits a test: "20 per minute" is 20 per 3 s, and the sweeper's
/// 30 s tick is 1.5 s. Every ratio that decides the outcome (burst vs interval, poll tick vs
/// interval) is preserved. Revisions are switched on for <c>SparkMessages</c>, so the number of
/// writes each message cost is read off the database rather than estimated.
/// </para>
/// <para>
/// The kept test asserts only what does not depend on timing — every message completes, and no
/// throttled message is deferred more than once — and prints the timing figures. The PRD records the
/// figures of the 1000-message run (<c>SPARK_SPIKE_SM3_BULK=1000</c>).
/// </para>
/// </summary>
public class ThrottleAccuracySpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private const string BulkQueue = "spike-bulk";
    private const string TransactionalQueue = "spike-transactional";
    private const int Compression = 20;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1) / Compression;
    private const int MaxPerInterval = 20;

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    [MessageQueue(BulkQueue)]
    public sealed record BulkMail(int Number);

    [MessageQueue(TransactionalQueue)]
    public sealed record ResetMail(DateTime PublishedAtUtc, bool Baseline = false);

    public sealed class Recorder
    {
        public ConcurrentQueue<DateTime> BulkStarts { get; } = new();
        public ConcurrentQueue<TimeSpan> TransactionalLatencies { get; } = new();
        public ConcurrentQueue<TimeSpan> BaselineLatencies { get; } = new();
    }

    public sealed class BulkRecipient(Recorder recorder) : IRecipient<BulkMail>
    {
        public Task HandleAsync(BulkMail message, CancellationToken cancellationToken = default)
        {
            recorder.BulkStarts.Enqueue(DateTime.UtcNow);
            return Task.CompletedTask;
        }
    }

    public sealed class ResetRecipient(Recorder recorder) : IRecipient<ResetMail>
    {
        public Task HandleAsync(ResetMail message, CancellationToken cancellationToken = default)
        {
            (message.Baseline ? recorder.BaselineLatencies : recorder.TransactionalLatencies).Enqueue(DateTime.UtcNow - message.PublishedAtUtc);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task S_M3_throttle_accuracy_alongside_transactional_traffic()
    {
        var bulkCount = int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_SM3_BULK"), out var n) ? n : 100;

        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                ["SparkMessages"] = new() { Disabled = false, MinimumRevisionsToKeep = 1000 },
            },
        }));

        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSingleton(recorder);
        services.AddSparkMessaging(o =>
        {
            o.FallbackPollInterval = TimeSpan.FromSeconds(30) / Compression;
            o.Queues[BulkQueue] = new SparkQueueOptions { MaxPerInterval = MaxPerInterval, Interval = Interval };
        });
        services.AddScoped<IRecipient<BulkMail>, BulkRecipient>();
        services.AddScoped<IRecipient<ResetMail>, ResetRecipient>();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        try
        {
            using var scope = provider.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

            // Baseline: transactional latency with no bulk backlog, same pacing. The first message also
            // absorbs subscription creation and is excluded.
            await bus.BroadcastAsync(new ResetMail(DateTime.UtcNow, Baseline: true));
            await AsyncWait.UntilAsync(() => recorder.BaselineLatencies.Count >= 1, "the warm-up message", TimeSpan.FromSeconds(30));
            recorder.BaselineLatencies.Clear();
            const int baselineCount = 20;
            for (var i = 0; i < baselineCount; i++)
            {
                await bus.BroadcastAsync(new ResetMail(DateTime.UtcNow, Baseline: true));
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            await AsyncWait.UntilAsync(() => recorder.BaselineLatencies.Count >= baselineCount, "the baseline messages", TimeSpan.FromSeconds(30));

            var publishStarted = DateTime.UtcNow;
            for (var i = 0; i < bulkCount; i++)
                await bus.BroadcastAsync(new BulkMail(i));
            var publishTook = DateTime.UtcNow - publishStarted;

            // Transactional traffic for as long as the bulk backlog drains: one reset mail every
            // 250 ms (5 s uncompressed).
            var transactionalSent = 0;
            using var stopTraffic = new CancellationTokenSource();
            var traffic = Task.Run(async () =>
            {
                while (!stopTraffic.IsCancellationRequested)
                {
                    await bus.BroadcastAsync(new ResetMail(DateTime.UtcNow));
                    Interlocked.Increment(ref transactionalSent);
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250), stopTraffic.Token); }
                    catch (OperationCanceledException) { }
                }
            });

            // Failure bound only: the expected drain is (n - 20) / 20 intervals, plus the sweeper tick.
            var bound = Interval * ((bulkCount / (double)MaxPerInterval) * 3) + TimeSpan.FromMinutes(1);
            await AsyncWait.UntilAsync(() => recorder.BulkStarts.Count >= bulkCount, "every bulk message to be handled", bound);
            await stopTraffic.CancelAsync();
            await traffic;
            await AsyncWait.UntilAsync(() => recorder.TransactionalLatencies.Count >= transactionalSent,
                "every transactional message to be handled", TimeSpan.FromSeconds(30));

            await Store.WaitForIndexingAsync();
            var writes = await WritesPerMessageAsync();

            var starts = recorder.BulkStarts.OrderBy(t => t).ToList();
            var steadyRate = starts.Count > MaxPerInterval
                ? (starts.Count - MaxPerInterval) / ((starts[^1] - starts[MaxPerInterval - 1]) / Interval)
                : double.NaN;
            var maxBurst = starts.Select((t, i) => starts.Skip(i).TakeWhile(s => s - t < Interval).Count()).Max();
            var latencies = recorder.TransactionalLatencies.OrderBy(t => t).ToList();
            var p95 = latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1];

            output.WriteLine($"S-M3 bulk={bulkCount} publishTook={publishTook.TotalMilliseconds:F0}ms target={MaxPerInterval}/interval interval={Interval.TotalSeconds}s poll={TimeSpan.FromSeconds(30).TotalSeconds / Compression}s");
            output.WriteLine($"S-M3 drain={(starts[^1] - starts[0]).TotalSeconds:F1}s steadyRate={steadyRate:F2}/interval maxStartsInAnyInterval={maxBurst}");
            var baseline = recorder.BaselineLatencies.OrderBy(t => t).ToList();
            output.WriteLine($"S-M3 baseline (no backlog) n={baseline.Count} p50={baseline[baseline.Count / 2].TotalMilliseconds:F0}ms p95={baseline[(int)Math.Ceiling(baseline.Count * 0.95) - 1].TotalMilliseconds:F0}ms max={baseline[^1].TotalMilliseconds:F0}ms");
            output.WriteLine($"S-M3 transactional n={latencies.Count} p50={latencies[latencies.Count / 2].TotalMilliseconds:F0}ms p95={p95.TotalMilliseconds:F0}ms max={latencies[^1].TotalMilliseconds:F0}ms");
            foreach (var (queue, perMessage) in writes)
                output.WriteLine($"S-M3 writes {queue}: {string.Join(", ", perMessage.GroupBy(w => w).OrderBy(g => g.Key).Select(g => $"{g.Key} writes x{g.Count()}"))}");

            // Timing-independent invariants. An unthrottled message costs a fixed number of writes; a
            // throttled one costs that plus exactly one deferral (defer + wake-up + re-claim).
            var unthrottledWrites = writes[TransactionalQueue].Max();
            writes[BulkQueue].Max().Should().BeLessThanOrEqualTo(unthrottledWrites + 3, "a throttled message is deferred at most once");
            writes[BulkQueue].Count.Should().Be(bulkCount);
        }
        finally
        {
            foreach (var service in hosted.AsEnumerable().Reverse())
                await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Revisions per message document, grouped by queue — one revision per write.</summary>
    private async Task<Dictionary<string, List<int>>> WritesPerMessageAsync()
    {
        using var session = Store.OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue; // one revisions count per message
        var messages = new List<SparkMessage>();
        await using (var stream = await session.Advanced.StreamAsync(session.Query<SparkMessage>()))
        {
            while (await stream.MoveNextAsync())
                messages.Add(stream.Current.Document);
        }

        messages.Should().OnlyContain(m => m.Status == EMessageStatus.Completed);
        var result = new Dictionary<string, List<int>> { [BulkQueue] = [], [TransactionalQueue] = [] };
        foreach (var message in messages)
        {
            var count = await session.Advanced.Revisions.GetCountForAsync(message.Id!);
            result[message.QueueName].Add((int)count);
        }

        return result;
    }
}
