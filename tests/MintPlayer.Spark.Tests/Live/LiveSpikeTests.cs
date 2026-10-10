using System.Diagnostics;
using System.Net.WebSockets;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Live;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// Spike S8 (PRD §8, research §5): measurements L2–L4 and L6, recorded in PRD §12 "Spike S8". Each prints its
/// numbers and asserts only the bound the design depends on, so a run on a loaded machine still passes while
/// the printed numbers are the evidence.
/// </summary>
/// <remarks>Fixture names start with <c>Sp</c>: <c>ActionsResolver</c> matches actions classes by simple name.</remarks>
public class LiveSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private static readonly Guid RunTypeId = Guid.Parse("5b8e0000-0000-4000-8000-000000000001");
    private static readonly Guid RunsQueryId = Guid.Parse("5b8e0000-0000-4000-8000-000000000011");
    private static readonly Uri Server = new("http://localhost/");

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory<SpContext> Start(Action<SparkLiveOptions>? live = null)
    {
        var factory = new SparkEndpointFactory<SpContext>(
            Store,
            [new EntityTypeFile
            {
                PersistentObject = new EntityTypeDefinition
                {
                    Id = RunTypeId,
                    Name = "SpRun",
                    ClrType = typeof(SpRun).FullName!,
                    Live = true,
                    Breadcrumb = "{Url}",
                    Attributes =
                    [
                        new() { Id = Guid.NewGuid(), Name = "Url", DataType = "string" },
                        new() { Id = Guid.NewGuid(), Name = "Status", DataType = "string" },
                        new() { Id = Guid.NewGuid(), Name = "Phase", DataType = "string" },
                        new() { Id = Guid.NewGuid(), Name = "CurrentUrl", DataType = "string" },
                        new() { Id = Guid.NewGuid(), Name = "PagesDone", DataType = "number" },
                        new() { Id = Guid.NewGuid(), Name = "PagesTotal", DataType = "number" },
                        new() { Id = Guid.NewGuid(), Name = "Score", DataType = "decimal" },
                        new() { Id = Guid.NewGuid(), Name = "StartedAt", DataType = "datetime" },
                    ],
                },
                Queries = [new SparkQuery { Id = RunsQueryId, Name = "SpRuns", Source = "Database.Runs", EntityType = "SpRun", Live = true }],
            }],
            configureSpark: live is null ? null : spark => MintPlayer.Spark.Extensions.SparkBuilderLiveExtensions.AddLive(spark, live));
        factories.Add(factory);
        return factory;
    }

    private async Task<List<SpRun>> SeedAsync(int count)
    {
        var runs = Enumerable.Range(0, count).Select(i => new SpRun
        {
            Url = $"https://site{i:D3}.example/",
            Status = "Running",
            Phase = "Crawling",
            CurrentUrl = $"https://site{i:D3}.example/page/{i}",
            PagesDone = i,
            PagesTotal = 500,
            Score = 50 + i % 50,
            StartedAt = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc).AddMinutes(i),
        }).ToList();
        using var session = Store.OpenAsyncSession();
        foreach (var run in runs)
            await session.StoreAsync(run);
        await session.SaveChangesAsync();
        return runs;
    }

    private async Task WriteAsync(string id, Action<SpRun> change)
    {
        using var session = Store.OpenAsyncSession();
        change((await session.LoadAsync<SpRun>(id))!);
        await session.SaveChangesAsync();
    }

    private static object Page(string sort = "Url", string direction = "asc")
        => new { queryId = RunsQueryId.ToString(), take = 50, sortColumns = new[] { new { property = sort, direction } } };

    // ---- L2: full page vs patch ------------------------------------------------------------------------------

    [Fact]
    public async Task L2_a_patch_is_a_fraction_of_the_full_page()
    {
        var factory = Start();
        var runs = await SeedAsync(50);
        await using var client = await LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server);
        await client.SubscribeQueryAsync("q", Page());
        await client.NextAsync("result", "q");
        var full = client.BytesReceived;

        await WriteAsync(runs[10].Id!, r => { r.PagesDone++; r.CurrentUrl += "/next"; });
        await client.NextAsync("patch", "q");
        var oneRow = client.BytesReceived - full;

        var before = client.BytesReceived;
        using (var session = Store.OpenAsyncSession())
        {
            foreach (var run in runs.Take(5))
                (await session.LoadAsync<SpRun>(run.Id!))!.PagesDone += 10;
            await session.SaveChangesAsync();
        }
        await client.NextAsync("patch", "q");
        var fiveRows = client.BytesReceived - before;

        before = client.BytesReceived;
        await WriteAsync(runs[0].Id!, r => r.Url = "https://zzz.example/"); // moves from first to last
        await client.NextAsync("patch", "q");
        var moved = client.BytesReceived - before;

        await client.SubscribePoAsync("p", RunTypeId.ToString(), runs[20].Id!);
        before = client.BytesReceived;
        await client.NextAsync("po", "p");
        var po = client.BytesReceived - before;
        before = client.BytesReceived;
        await WriteAsync(runs[20].Id!, r => r.PagesDone++);
        // The PO and the page both change: take both frames.
        await client.NextAsync("poPatch", "p");
        var poPatch = client.BytesReceived - before;

        output.WriteLine($"L2 50-row page, 8 columns: full result {full} B; patch 1 row {oneRow} B; 5 rows {fiveRows} B; row moved first→last {moved} B");
        output.WriteLine($"L2 persistent object (8 attributes): full po {po} B; poPatch (one attribute + etag, page patch included) {poPatch} B");
        oneRow.Should().BeLessThan(full / 10, "a one-row change is a small fraction of the page");
    }

    // ---- L3: cost per push with 1/10/50 viewers --------------------------------------------------------------

    [Fact]
    public async Task L3_cost_per_push_with_1_10_and_50_viewers_of_one_document_written_at_10_Hz()
    {
        foreach (var throttle in new[] { 500, 250 })
        {
            foreach (var viewers in new[] { 1, 10, 50 })
            {
                var factory = Start(o => { o.Throttle = TimeSpan.FromMilliseconds(throttle); o.MaxSocketsPerUser = 100; });
                var run = (await SeedAsync(1))[0];
                var clients = new List<LiveTestClient>();
                for (var i = 0; i < viewers; i++)
                {
                    var client = await LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server);
                    await client.SubscribePoAsync("p", RunTypeId.ToString(), run.Id!);
                    await client.NextAsync("po", "p");
                    clients.Add(client);
                }

                var executor = Store.GetRequestExecutor();
                var requestsBefore = executor.NumberOfServerRequests;
                var evaluations = Interlocked.Read(ref LiveConnection.Evaluations);
                var ticks = Interlocked.Read(ref LiveConnection.EvaluationTicks);
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var framesBefore = clients.Sum(c => c.FramesReceived);

                // 10 Hz for 3 s.
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < 30; i++)
                {
                    await WriteAsync(run.Id!, r => r.PagesDone++);
                    var next = TimeSpan.FromMilliseconds(100 * (i + 1)) - clock.Elapsed;
                    if (next > TimeSpan.Zero)
                        await Task.Delay(next);
                }
                // Drain the trailing pushes.
                await Task.WhenAll(clients.Select(c => DrainAsync(c, TimeSpan.FromSeconds(1.5))));

                var requests = executor.NumberOfServerRequests - requestsBefore;
                var ran = Interlocked.Read(ref LiveConnection.Evaluations) - evaluations;
                var ms = (Interlocked.Read(ref LiveConnection.EvaluationTicks) - ticks) * 1000.0 / Stopwatch.Frequency;
                var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                var frames = clients.Sum(c => c.FramesReceived) - framesBefore;
                output.WriteLine($"L3 throttle {throttle} ms, {viewers,2} viewers, 30 writes in {clock.Elapsed.TotalSeconds:0.0} s: {ran} evaluations ({ran / (double)viewers:0.0}/viewer), {ms / Math.Max(1, ran):0.00} ms/evaluation, {frames} frames, {requests} RavenDB requests ({(requests - 30) / (double)Math.Max(1, ran):0.0}/evaluation), test-process CPU {cpuMs:0} ms ({cpuMs / Math.Max(1, ran):0.00} ms/evaluation)");

                // At most one push per viewer per throttle window, plus leading and trailing edges (frames are per
                // client; the evaluation counters are process-wide, so run this class alone for exact numbers).
                (frames / (double)viewers).Should().BeLessThanOrEqualTo(clock.Elapsed.TotalSeconds / (throttle / 1000.0) + 3);

                foreach (var client in clients)
                    await client.DisposeAsync();
                await factory.DisposeAsync();
                factories.Remove(factory);
            }
        }
    }

    private static async Task DrainAsync(LiveTestClient client, TimeSpan quiet)
    {
        while (await client.TryNextAsync(quiet) is not null) { }
    }

    // ---- L4: a client that stops reading -------------------------------------------------------------------

    [Fact]
    public async Task L4_a_client_that_stops_reading_is_aborted_at_the_send_timeout_and_nothing_queues_meanwhile()
    {
        var factory = Start(o => { o.Heartbeat = TimeSpan.FromMilliseconds(100); o.SendTimeout = TimeSpan.FromSeconds(1); });
        using var scope = factory.CreateScope();
        var socket = new StuckWebSocket();

        var clock = Stopwatch.StartNew();
        var connection = new LiveConnection(socket, scope.ServiceProvider, DateTimeOffset.UtcNow.AddHours(1));
        await connection.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Stop();

        output.WriteLine($"L4 heartbeat 100 ms, send timeout 1 s: aborted after {clock.ElapsedMilliseconds} ms; {socket.SendsStarted} send started, {socket.SendsCompleted} completed");
        socket.Aborted.Should().BeTrue("a frame not taken within SendTimeout aborts the socket");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        socket.SendsStarted.Should().Be(1, "the worker sends inline: while one frame is stuck, nothing else is produced or queued");
    }

    /// <summary>A socket whose peer never reads: every send waits until cancelled.</summary>
    private sealed class StuckWebSocket : WebSocket
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState state = WebSocketState.Open;
        public int SendsStarted, SendsCompleted;
        public bool Aborted;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            Aborted = true;
            state = WebSocketState.Aborted;
            closed.TrySetResult();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override void Dispose() => Abort();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await closed.Task.WaitAsync(cancellationToken);
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SendsStarted);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            Interlocked.Increment(ref SendsCompleted);
        }
    }

    // ---- L6: index freshness ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task L6_a_pushed_index_page_reflects_the_write_that_triggered_it(bool waitForIndexes)
    {
        // Sorted by Score: an auto index, which is eventually consistent. Each write moves one run to the top.
        var factory = Start(o =>
        {
            o.Source = SparkLiveSource.Actions; // only the in-process event: no Changes echo to re-run a stale page
            o.Revalidate = TimeSpan.FromMinutes(10);
            if (!waitForIndexes)
                o.IndexWait = TimeSpan.FromTicks(1);
        });
        var runs = await SeedAsync(50);
        await using var client = await LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server);
        await client.SubscribeQueryAsync("q", Page("Score", "desc"));
        await client.NextAsync("result", "q");

        int fresh = 0, missed = 0, stale = 0;
        for (var i = 0; i < 20; i++)
        {
            var target = runs[(i * 7) % runs.Count];
            await WriteAsync(target.Id!, r => r.Score = 1000 + i);
            var frame = await client.TryNextAsync(TimeSpan.FromSeconds(2));
            if (frame is null)
            {
                missed++;
                continue;
            }
            var patch = frame.Value.GetProperty(frame.Value.GetProperty("type").GetString() == "patch" ? "patch" : "result");
            var top = patch.TryGetProperty("order", out var order) ? order[0].GetString()
                : patch.TryGetProperty("items", out var items) ? items[0].GetProperty("id").GetString()
                : null;
            if (string.Equals(top, target.Id, StringComparison.OrdinalIgnoreCase)) fresh++; else stale++;
            await Task.Delay(550); // past the throttle, so every write gets its own leading-edge evaluation
        }

        output.WriteLine($"L6 index wait {(waitForIndexes ? "on (5 s bound)" : "off")}: of 20 writes, {fresh} pushes fresh, {stale} stale, {missed} never pushed (stale evaluation diffed as unchanged)");
        if (waitForIndexes)
            fresh.Should().Be(20, "each evaluation waits for the indexes over the changed collection first");
    }
}

public class SpRun
{
    public string? Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string CurrentUrl { get; set; } = string.Empty;
    public int PagesDone { get; set; }
    public int PagesTotal { get; set; }
    public decimal Score { get; set; }
    public DateTime StartedAt { get; set; }
}

public class SpContext : SparkContext
{
    public IRavenQueryable<SpRun> Runs => Session.Query<SpRun>();
}

public class SpRunActions(MintPlayer.Spark.Services.IEntityMapper mapper) : DefaultPersistentObjectActions<SpRun>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Spike fixture: every run is public.";
}
