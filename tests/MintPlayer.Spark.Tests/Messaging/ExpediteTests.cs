using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// R29 (dependency-updates PRD §13.13.3): <see cref="BroadcastOptions.Expedite"/>. A lane takes its oldest
/// expedited message before any normal one, stays serial, and serves one ready normal message after
/// <see cref="LaneBuffer.StarvationCap"/> expedited ones in a row. The flag is persisted on every publish
/// path, sorted by the feeder's page, and needs no subscription of its own.
/// </summary>
public class ExpediteTests(ITestOutputHelper output) : SparkTestDriver
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    // ---- the lane buffer ------------------------------------------------------------------------

    private static async Task<List<string>> DrainAsync(LaneBuffer buffer)
    {
        buffer.TryComplete();
        var taken = new List<string>();
        await foreach (var id in buffer.ReadAllAsync())
            taken.Add(id);
        return taken;
    }

    [Fact]
    public async Task A_lane_serves_its_oldest_expedited_message_before_any_normal_one()
    {
        var buffer = new LaneBuffer(64);
        await buffer.WriteAsync("n1", expedite: false, CancellationToken.None);
        await buffer.WriteAsync("n2", expedite: false, CancellationToken.None);
        await buffer.WriteAsync("e1", expedite: true, CancellationToken.None);
        await buffer.WriteAsync("n3", expedite: false, CancellationToken.None);
        await buffer.WriteAsync("e2", expedite: true, CancellationToken.None);

        (await DrainAsync(buffer)).Should().Equal(["e1", "e2", "n1", "n2", "n3"], "expedited first, each kind FIFO");
    }

    [Fact]
    public async Task After_ten_expedited_messages_in_a_row_one_ready_normal_message_goes_next()
    {
        var buffer = new LaneBuffer(64);
        for (var i = 1; i <= 3; i++)
            await buffer.WriteAsync($"n{i}", expedite: false, CancellationToken.None);
        for (var i = 1; i <= 22; i++)
            await buffer.WriteAsync($"e{i}", expedite: true, CancellationToken.None);

        var expected = new List<string>();
        expected.AddRange(Enumerable.Range(1, 10).Select(i => $"e{i}"));
        expected.Add("n1");
        expected.AddRange(Enumerable.Range(11, 10).Select(i => $"e{i}"));
        expected.Add("n2");
        expected.AddRange(["e21", "e22", "n3"]);

        (await DrainAsync(buffer)).Should().Equal(expected);
        LaneBuffer.StarvationCap.Should().Be(10);
    }

    [Fact]
    public async Task With_no_normal_message_ready_expedited_ones_keep_going_and_the_next_normal_one_goes_first()
    {
        var buffer = new LaneBuffer(64);
        for (var i = 1; i <= 12; i++)
            await buffer.WriteAsync($"e{i}", expedite: true, CancellationToken.None);

        var reader = buffer.ReadAllAsync().GetAsyncEnumerator();
        var taken = new List<string>();
        for (var i = 0; i < 11; i++)
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            taken.Add(reader.Current);
        }
        taken.Should().Equal(Enumerable.Range(1, 11).Select(i => $"e{i}"), "nothing normal was waiting, so nothing was starved");

        await buffer.WriteAsync("n1", expedite: false, CancellationToken.None);
        await buffer.WriteAsync("e13", expedite: true, CancellationToken.None);
        buffer.TryComplete();
        while (await reader.MoveNextAsync())
            taken.Add(reader.Current);

        taken.Skip(11).Should().Equal(["n1", "e12", "e13"], "the streak was past the cap, so the first ready normal message goes next");
    }

    [Fact]
    public async Task The_starvation_counter_is_shared_by_every_pump_of_a_lane()
    {
        // MaxConcurrency > 1: two pumps read one lane. The cap is the lane's, not each pump's.
        var buffer = new LaneBuffer(64);
        await buffer.WriteAsync("n1", expedite: false, CancellationToken.None);
        for (var i = 1; i <= 12; i++)
            await buffer.WriteAsync($"e{i}", expedite: true, CancellationToken.None);
        buffer.TryComplete();

        var pumps = new[] { buffer.ReadAllAsync().GetAsyncEnumerator(), buffer.ReadAllAsync().GetAsyncEnumerator() };
        var taken = new List<string>();
        for (var turn = 0; ; turn++)
        {
            var pump = pumps[turn % 2];
            if (!await pump.MoveNextAsync())
                break;
            taken.Add(pump.Current);
        }

        taken.IndexOf("n1").Should().Be(10, "ten expedited messages were taken in a row across both pumps");
        taken.Should().HaveCount(13);
    }

    [Fact]
    public async Task Expedited_and_normal_messages_share_the_lanes_capacity()
    {
        var buffer = new LaneBuffer(2);
        await buffer.WriteAsync("n1", expedite: false, CancellationToken.None);
        await buffer.WriteAsync("e1", expedite: true, CancellationToken.None);

        var third = buffer.WriteAsync("e2", expedite: true, CancellationToken.None).AsTask();
        await Task.Delay(100);
        third.IsCompleted.Should().BeFalse("a full lane back-pressures the feeder, whatever the flag");

        var reader = buffer.ReadAllAsync().GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Should().Be("e1");
        await third.WaitAsync(Timeout);
        buffer.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_completed_lane_refuses_writes_and_drains_what_it_holds()
    {
        var buffer = new LaneBuffer(1);
        await buffer.WriteAsync("n1", expedite: false, CancellationToken.None);
        var blocked = buffer.WriteAsync("n2", expedite: false, CancellationToken.None).AsTask();

        buffer.TryComplete().Should().BeTrue();
        buffer.TryComplete().Should().BeFalse();

        await ((Func<Task>)(() => blocked)).Should().ThrowAsync<ChannelClosedException>();
        await ((Func<Task>)(() => buffer.WriteAsync("n3", expedite: true, CancellationToken.None).AsTask())).Should().ThrowAsync<ChannelClosedException>();

        var taken = new List<string>();
        await foreach (var id in buffer.ReadAllAsync())
            taken.Add(id);
        taken.Should().Equal(["n1"]);
    }

    // ---- the feeder's order ---------------------------------------------------------------------

    private static MessageFeeder.Candidate C(string id, int priority, int sequence, bool expedite = false)
        => new(id, "q", priority, T0.AddTicks(sequence), expedite);

    [Fact]
    public void The_feeder_claims_expedited_messages_first_within_a_queue_priority_never_across_one()
    {
        var groups = MessageFeeder.ServeOrder(
            [C("n1", 0, 1), C("e1", 0, 5, expedite: true), C("h1", 1, 9)],
            [C("n2", 0, 2), C("l1", -1, 0, expedite: true)]);

        groups.Select(g => string.Join(",", g.Select(c => c.Id))).Should().Equal(["h1", "e1,n1,n2", "l1"],
            "the queue's priority still ranks first; Expedite orders within it");
    }

    // ---- persisted on every publish path --------------------------------------------------------

    private record Onboard(string RepositoryId);

    private MessageBus NewBus() => new(Store, Options.Create(new SparkMessagingOptions()));

    private async Task<SparkMessage> SingleAsync(string repositoryId)
    {
        using var session = Store.OpenAsyncSession();
        var all = await session.Advanced.LoadStartingWithAsync<SparkMessage>("SparkMessages/", pageSize: 1024);
        return all.Single(m => m.PayloadJson.Contains(repositoryId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expedite_is_persisted_by_BroadcastAsync_BroadcastOnceAsync_and_the_outbox()
    {
        IMessageBus bus = NewBus();
        await bus.BroadcastAsync(new Onboard("plain"));
        await bus.BroadcastAsync(new Onboard("broadcast"), new BroadcastOptions { Expedite = true });
        await bus.BroadcastOnceAsync(new Onboard("once"), "dependency-onboard:once", new BroadcastOptions { Expedite = true });
        await bus.BroadcastOnceAsync(new Onboard("once"), "dependency-onboard:once", new BroadcastOptions { Expedite = true });
        using (var session = Store.OpenAsyncSession())
        {
            await ((IMessageOutbox)bus).EnqueueAsync(session, new Onboard("outbox"), new BroadcastOptions { Expedite = true });
            await session.SaveChangesAsync();
        }

        (await SingleAsync("plain")).Expedite.Should().BeFalse("normal is the default");
        (await SingleAsync("broadcast")).Expedite.Should().BeTrue();
        var once = await SingleAsync("once");
        once.Expedite.Should().BeTrue("the flag survives the deduplicated path, and the second publish was a no-op");
        once.Id.Should().Be(MessageBus.DeduplicationId(typeof(Onboard), "dependency-onboard:once"));
        (await SingleAsync("outbox")).Expedite.Should().BeTrue();
    }

    [Fact]
    public async Task BroadcastOnceAsync_with_options_still_requires_a_key()
    {
        IMessageBus bus = NewBus();

        var act = () => bus.BroadcastOnceAsync(new Onboard("x"), " ", new BroadcastOptions { Expedite = true });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- a serial lane, through the router ------------------------------------------------------

    public sealed class Job { public string? Name { get; set; } }

    public sealed class JobRecipient : IRecipient<Job>
    {
        public ConcurrentQueue<string> Order { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int running;
        public int MaxRunning;

        public async Task HandleAsync(Job message, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref MaxRunning, now);
            Order.Enqueue(message.Name!);
            if (message.Name == "blocker")
            {
                BlockerStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            Interlocked.Decrement(ref running);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    private sealed class AllowAll : IMessageTypeAllowList
    {
        public bool IsAllowedMessageType(string? assemblyQualifiedName) => true;
        public bool IsAllowedHandlerType(string? assemblyQualifiedName) => true;
    }

    private async Task<string> SeedClaimedJobAsync(string name)
    {
        var message = new SparkMessage
        {
            QueueName = "expedite-lane",
            MessageType = typeof(Job).AssemblyQualifiedName!,
            PayloadJson = JsonConvert.SerializeObject(new Job { Name = name }),
            CreatedAtUtc = DateTime.UtcNow,
            MaxAttempts = 3,
            AttemptCount = 1,
            Status = EMessageStatus.Processing,
            OwnerId = MessageClaims.NodeId,
            ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        };
        await SeedAsync(session => session.StoreAsync(message));
        return message.Id!;
    }

    [Fact]
    public async Task An_expedited_message_routed_behind_normal_ones_runs_next_and_the_lane_stays_serial()
    {
        var recipient = new JobRecipient();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageTypeAllowList>(new AllowAll());
        services.AddSingleton<IRecipient<Job>>(recipient);
        await using var provider = services.BuildServiceProvider();
        var options = Options.Create(new SparkMessagingOptions { ClaimTtl = TimeSpan.FromMinutes(5), HandlerTimeout = TimeSpan.FromMinutes(1) });
        var processor = new MessageProcessor(provider, Store, options, NullLogger<MessageProcessor>.Instance);
        await using var router = new MessageQueueRouter(processor, Store, options, NullLogger<MessageQueueRouter>.Instance);
        router.Start(CancellationToken.None);

        var ids = new Dictionary<string, string>();
        foreach (var name in new[] { "blocker", "n1", "n2", "e1", "n3", "e2" })
            ids[name] = await SeedClaimedJobAsync(name);

        await router.RouteAsync("expedite-lane", ids["blocker"], CancellationToken.None);
        await recipient.BlockerStarted.Task.WaitAsync(Timeout);
        foreach (var name in new[] { "n1", "n2", "e1", "n3", "e2" })
            await router.RouteAsync("expedite-lane", ids[name], expedite: name.StartsWith('e'), CancellationToken.None);
        router.BufferedCount("expedite-lane").Should().Be(5);

        recipient.Release.SetResult();
        await AsyncWait.UntilAsync(() => recipient.Order.Count == 6, "the lane to run all six", Timeout);
        await router.DrainAsync(Timeout);

        recipient.Order.ToList().Should().Equal(["blocker", "e1", "e2", "n1", "n2", "n3"]);
        recipient.MaxRunning.Should().Be(1, "expediting reorders a serial lane, it never runs two at once");
    }

    // ---- the real pipeline: the feeder's page ---------------------------------------------------

    [MessageQueue("expedite-pipeline")]
    public sealed record PipelineWork(int Number);

    public sealed class PipelineRecipient(ConcurrentBag<int> seen) : IRecipient<PipelineWork>
    {
        public Task HandleAsync(PipelineWork message, CancellationToken cancellationToken = default) { seen.Add(message.Number); return Task.CompletedTask; }
    }

    [Fact]
    public async Task An_expedited_message_published_behind_more_than_a_page_of_its_own_queue_is_claimed_first()
    {
        // The index change: without Expedite in SparkMessages_ByPriority's sort, the first page is the
        // 256 oldest normal messages and the expedited one waits for the subscription to reach it.
        const int normalCount = 300;
        var seen = new ConcurrentBag<int>();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new TestOutputLoggerProvider(output)));
        services.AddSingleton(Store);
        services.AddSingleton(seen);
        services.AddSparkMessaging(o => o.FallbackPollInterval = TimeSpan.FromSeconds(1));
        services.AddScoped<IRecipient<PipelineWork>, PipelineRecipient>();
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            for (var i = 0; i < normalCount; i++)
                await bus.BroadcastAsync(new PipelineWork(i));
            await bus.BroadcastAsync(new PipelineWork(-1), new BroadcastOptions { Expedite = true });
        }
        await WaitForIndexesAsync();

        var expeditedId = await ExpeditedIdAsync();
        var routed = new ConcurrentQueue<string>();
        provider.GetRequiredService<MessageQueueRouter>().Routed += (_, id) => routed.Enqueue(id);
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(() => seen.Count >= normalCount + 1, "the queue to drain", TimeSpan.FromSeconds(60));

            routed.Should().HaveCount(normalCount + 1);
            routed.First().Should().Be(expeditedId, "an expedited message beats a normal backlog longer than FeederBatchSize (256)");
        }
        finally
        {
            foreach (var service in hosted.AsEnumerable().Reverse())
                await service.StopAsync(CancellationToken.None);
        }
    }

    private async Task<string> ExpeditedIdAsync()
    {
        using var session = Store.OpenAsyncSession();
        var all = await session.Advanced.LoadStartingWithAsync<SparkMessage>("SparkMessages/", pageSize: 1024);
        return all.Single(m => m.Expedite).Id!;
    }
}
