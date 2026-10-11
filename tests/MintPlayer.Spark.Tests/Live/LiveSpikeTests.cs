using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private SparkEndpointFactory<SpContext> Start(Action<SparkLiveOptions>? live = null, LiveLogRecorder? logs = null)
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
            configureServices: logs is null ? null : services =>
            {
                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
                services.AddLogging(b => b.AddFilter<LiveLogRecorder>(typeof(LiveConnection).FullName, Microsoft.Extensions.Logging.LogLevel.Debug));
            },
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
        // The page holds all 50 runs, so every write changes a row on it and every evaluation sends a frame: a
        // stale read sends the row's new values (loaded from the document) without moving it (the index's
        // order). Frames are matched to their write by reading until the written row is on top, so a late
        // frame is never counted against the next write.
        var logs = new LiveLogRecorder();
        var factory = Start(o =>
        {
            o.Source = SparkLiveSource.Actions; // only the in-process event: no Changes echo to re-run a stale page
            o.Revalidate = TimeSpan.FromMinutes(10);
            if (!waitForIndexes)
                o.IndexWait = TimeSpan.FromTicks(1);
        }, logs);
        var runs = await SeedAsync(50);
        await using var client = await LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server);
        await client.SubscribeQueryAsync("q", Page("Score", "desc"));
        await client.NextAsync("result", "q");

        // On: the 5 s wait, then a re-read after the throttle's back-off if it ran out. Off: every read may be stale and
        // is read again 500 ms later. Either way the written row reaches the top well within the bound.
        var bound = TimeSpan.FromSeconds(15);
        int fresh = 0, corrected = 0, never = 0;
        var laps = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var target = runs[(i * 7) % runs.Count];
            var lap = Stopwatch.StartNew();
            await WriteAsync(target.Id!, r => r.Score = 1000 + i);
            var written = lap.ElapsedMilliseconds;
            var frames = new List<string>();
            var onTop = false;
            while (!onTop && lap.Elapsed < bound && await client.TryNextAsync(bound - lap.Elapsed) is { } frame)
            {
                var top = Top(frame);
                onTop = string.Equals(top, target.Id, StringComparison.OrdinalIgnoreCase);
                frames.Add($"{(onTop ? "on top" : top is null ? "values only" : "other top")} at {lap.ElapsedMilliseconds} ms");
            }
            if (!onTop) never++;
            else if (frames.Count == 1) fresh++;
            else corrected++;
            laps.Add($"#{i} write {written} ms; {string.Join(", ", frames.DefaultIfEmpty("no frame"))}");
            await Task.Delay(550); // past the throttle, so every write gets its own leading-edge evaluation
        }

        var ranOut = logs.Count(Microsoft.Extensions.Logging.LogLevel.Warning, "did not catch up");
        output.WriteLine($"L6 index wait {(waitForIndexes ? "on (5 s bound)" : "off")}: of 20 writes, {fresh} first push fresh, {corrected} first push stale then re-read, {never} never on top within {bound.TotalSeconds:0} s; index wait ran out {ranOut}×");
        foreach (var lap in laps)
            output.WriteLine("  " + lap);
        foreach (var line in logs.Lines)
            output.WriteLine("  log: " + line);
        never.Should().Be(0, "a page read before its index caught up is read again once it has");
        if (waitForIndexes)
            corrected.Should().BeLessThanOrEqualTo(ranOut, "a first push is stale only when the index wait ran out first");
    }

    [Fact]
    public async Task L6_a_page_read_before_its_index_caught_up_is_pushed_again_once_it_has()
    {
        // The index wait's bound runs out (here: the index is paused, so it cannot catch up), the evaluation
        // reads the page stale and finds nothing to send. Nothing else will trigger: no further write, and
        // Revalidate is 10 minutes away. The page must still reach the viewer once the index caught up.
        var factory = Start(o =>
        {
            o.Source = SparkLiveSource.Actions;
            o.Revalidate = TimeSpan.FromMinutes(10);
            o.IndexWait = TimeSpan.FromMilliseconds(300);
        });
        var runs = await SeedAsync(50);
        await using var client = await LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server);
        await client.SubscribeQueryAsync("q", Page("Score", "desc"));
        await client.NextAsync("result", "q");

        var indexes = await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.GetIndexesStatisticsOperation());
        var index = indexes.Single(i => i.Collections?.ContainsKey("SpRuns") == true).Name;
        await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StopIndexOperation(index));

        // The stale read loads the row's new values from the document but takes its place from the index: a
        // values-only patch arrives, the row is not moved to the top.
        var target = runs[17];
        await WriteAsync(target.Id!, r => r.Score = 5000);
        var whilePaused = new List<string>();
        while (await client.TryNextAsync(TimeSpan.FromSeconds(1)) is { } early)
            whilePaused.Add($"{early.GetProperty("type").GetString()} top={Top(early) ?? "(order unchanged)"}");
        output.WriteLine($"While paused: {string.Join("; ", whilePaused.DefaultIfEmpty("no frame"))}");
        whilePaused.Should().NotContain(f => f.Contains(target.Id!), "the paused index cannot have moved the row yet");

        var resumed = Stopwatch.StartNew();
        await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StartIndexOperation(index));
        string? top = null;
        while (resumed.Elapsed < TimeSpan.FromSeconds(10) && await client.TryNextAsync(TimeSpan.FromSeconds(10) - resumed.Elapsed) is { } frame)
        {
            top = Top(frame);
            output.WriteLine($"After resume, at {resumed.ElapsedMilliseconds} ms: {frame.GetProperty("type").GetString()} top={top ?? "(order unchanged)"}");
            if (top is not null)
                break;
        }
        top.Should().Be(target.Id, "an evaluation that read the index before it caught up is re-run once it has");
    }

    /// <summary>The first row id of a <c>result</c>, or of a <c>patch</c> that reorders; null for a values-only patch.</summary>
    private static string? Top(System.Text.Json.JsonElement frame)
    {
        var payload = frame.GetProperty(frame.GetProperty("type").GetString() == "patch" ? "patch" : "result");
        return payload.TryGetProperty("order", out var order) ? order[0].GetString()
            : payload.TryGetProperty("items", out var items) ? items[0].GetProperty("id").GetString()
            : null;
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

/// <summary>Records the live engine's log lines (the index wait's outcome per evaluation) with their time.</summary>
public sealed class LiveLogRecorder : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Line)> entries = new();

    public IEnumerable<string> Lines => entries.Select(e => e.Line);

    public int Count(Microsoft.Extensions.Logging.LogLevel level, string fragment)
        => entries.Count(e => e.Level == level && e.Line.Contains(fragment, StringComparison.Ordinal));

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(LiveLogRecorder recorder, string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => category == typeof(LiveConnection).FullName;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                recorder.entries.Enqueue((logLevel, $"{recorder.clock.ElapsedMilliseconds} ms [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" -- {exception.GetType().Name}: {exception.Message.Split('\n')[0].Trim()}")}"));
        }
    }
}
