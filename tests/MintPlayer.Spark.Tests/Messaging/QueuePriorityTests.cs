using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// #460 M16/M16b: priority in the single feeder — strict priority over the whole claimable set (the
/// sorted page), FIFO per queue by server-assigned sequence, every delivered message served in the
/// batch that delivered it (the no-starvation argument), the admission decision taken before the
/// claim, and configuration beating code for <c>Priority</c>.
/// </summary>
public class QueuePriorityTests(ITestOutputHelper output) : SparkTestDriver
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    private static MessageFeeder.Candidate C(string id, string queue, int priority, int sequence)
        => new(id, queue, priority, T0.AddTicks(sequence));

    [Fact]
    public void A_wake_up_is_served_highest_priority_first_then_in_sequence_order()
    {
        var page = new[] { C("u2", "urgent", 1, 7), C("n1", "plain", 0, 2), C("u1", "urgent", 1, 4) };
        var batch = new[] { C("b1", "bulk", -1, 1), C("n1", "plain", 0, 2), C("b2", "bulk", -1, 3), C("n2", "plain", 0, 5) };

        var groups = MessageFeeder.ServeOrder(page, batch);

        groups.Select(g => string.Join(",", g.Select(c => c.Id))).Should().Equal(["u1,u2", "n1,n2", "b1,b2"],
            "priority first, then sequence, and a message in both the page and the batch is served once");
    }

    [Fact]
    public void Ties_on_sequence_are_broken_by_id()
    {
        var groups = MessageFeeder.ServeOrder([C("m2", "q", 0, 1), C("m1", "q", 0, 1)], []);

        groups.Single().Select(c => c.Id).Should().Equal(["m1", "m2"]);
    }

    [Fact]
    public void Every_delivered_message_is_served_whatever_the_page_holds()
    {
        // The no-starvation argument: a page full of High messages does not push the batch's Low
        // messages out. The subscription walks the queue in commit order, and each message it delivers
        // is served in that batch, so a Low message waits at most for the subscription to reach it.
        var random = new Random(460);
        var page = Enumerable.Range(0, 256).Select(i => C($"h{i}", "high", 1, 1_000 + i)).ToList();
        var queues = new[] { ("high", 1), ("plain", 0), ("low", -1) };
        var batch = Enumerable.Range(0, 256).Select(i => { var (q, p) = queues[random.Next(3)]; return C($"d{i}", q, p, i); }).ToList();

        var served = MessageFeeder.ServeOrder(page, batch).SelectMany(g => g).ToList();

        batch.Select(c => c.Id).Except(served.Select(c => c.Id)).Should().BeEmpty("every delivered message is served");
        served.Should().HaveCount(page.Count + batch.Count);
        foreach (var (queue, _) in queues)
            served.Where(c => c.QueueName == queue).Select(c => c.Sequence).Should().BeInAscendingOrder("FIFO within a queue");
    }

    [Fact]
    public void Only_a_message_the_subscription_would_deliver_is_claimable()
    {
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Pending }).Should().BeTrue();
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Pending, NextAttemptAtUtc = T0, WakeUp = true }).Should().BeTrue();
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Failed, WakeUp = true }).Should().BeTrue();
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Pending, NextAttemptAtUtc = T0 }).Should().BeFalse("deferred or delayed");
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Failed }).Should().BeFalse("parked for a retry");
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Processing }).Should().BeFalse();
        MessageFeeder.IsClaimableNow(new SparkMessage { Status = EMessageStatus.Completed, WakeUp = true }).Should().BeFalse("a stale page entry");
    }

    [Fact]
    public void The_sequence_of_a_document_in_hand_is_the_captured_time_else_the_servers_last_modified()
    {
        var metadata = new Raven.Client.Json.MetadataAsDictionary { ["@last-modified"] = "2026-09-29T12:00:01.2345678Z" };

        MessageFeeder.SequenceOf(new SparkMessage(), metadata).Should().Be(new DateTime(2026, 9, 29, 12, 0, 1, DateTimeKind.Utc).AddTicks(2345678));
        MessageFeeder.SequenceOf(new SparkMessage { QueuedAtUtc = T0 }, metadata).Should().Be(T0);
    }

    [Fact]
    public void A_message_admitted_before_its_claim_is_not_charged_a_second_slot_by_the_processor()
    {
        var admission = new QueueAdmission();
        var oneAMinute = new SparkQueueOptions { MaxPerInterval = 1, Interval = TimeSpan.FromMinutes(1) };

        admission.DecideBeforeClaim("q", "m1", oneAMinute, T0, null).Kind.Should().Be(AdmissionKind.Admit);
        admission.Decide("q", "m1", oneAMinute, T0, null).Kind.Should().Be(AdmissionKind.Admit, "the feeder's admission is honoured after the claim");
        var next = admission.DecideBeforeClaim("q", "m2", oneAMinute, T0, null);

        next.Kind.Should().Be(AdmissionKind.Defer);
        next.Slot.Should().Be(T0.AddMinutes(1), "m1 used one slot, not two");
        admission.DecideBeforeClaim("q", "m2", oneAMinute, T0.AddMinutes(1), null).Kind.Should().Be(AdmissionKind.Admit, "the deferred message starts on its slot");
    }

    [Fact]
    public void Priority_and_the_window_bind_and_configuration_beats_a_library_default()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:Messaging:FeederBatchSize"] = "64",
            ["Spark:Messaging:Queues:mail-bulk:Priority"] = "Normal",
            ["Spark:Messaging:Queues:reports:Priority"] = "-1",
        }).Build();
        var services = new ServiceCollection();
        new MintPlayer.Spark.SparkBuilder(services, configuration).AddMessaging();
        // A library declaring its lanes after the app, as MailManager does.
        services.Configure<SparkMessagingOptions>(o =>
        {
            o.Queues.TryAdd("mail-bulk", new SparkQueueOptions { Priority = SparkQueuePriority.Low });
            o.Queues.TryAdd("mail-transactional", new SparkQueueOptions { Priority = SparkQueuePriority.High });
        });

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<SparkMessagingOptions>>().Value;

        options.FeederBatchSize.Should().Be(64);
        options.PriorityFor("mail-bulk").Should().Be(SparkQueuePriority.Normal, "configuration beats the library's Low");
        options.PriorityFor("mail-transactional").Should().Be(SparkQueuePriority.High);
        options.PriorityFor("reports").Should().Be(SparkQueuePriority.Low, "bound by number too");
        options.PriorityFor("undeclared").Should().Be(SparkQueuePriority.Normal);
        new SparkMessagingOptions().FeederBatchSize.Should().Be(SparkMessagingOptions.DefaultFeederBatchSize);
    }

    // ---- the real pipeline ------------------------------------------------------------------------

    [MessageQueue("prio-low")]
    public sealed record LowWork(int Number);

    [MessageQueue("prio-high")]
    public sealed record HighWork(int Number);

    public sealed class Seen
    {
        public ConcurrentBag<int> Low { get; } = [];
        public ConcurrentBag<int> High { get; } = [];
    }

    public sealed class LowRecipient(Seen seen) : IRecipient<LowWork>
    {
        public Task HandleAsync(LowWork message, CancellationToken cancellationToken = default) { seen.Low.Add(message.Number); return Task.CompletedTask; }
    }

    public sealed class HighRecipient(Seen seen) : IRecipient<HighWork>
    {
        public Task HandleAsync(HighWork message, CancellationToken cancellationToken = default) { seen.High.Add(message.Number); return Task.CompletedTask; }
    }

    private ServiceProvider BuildHost(Seen seen, Action<SparkMessagingOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new TestOutputLoggerProvider(output)));
        services.AddSingleton(Store);
        services.AddSingleton(seen);
        services.AddSparkMessaging(o =>
        {
            o.FallbackPollInterval = TimeSpan.FromSeconds(1);
            o.Queues["prio-low"] = new SparkQueueOptions { Priority = SparkQueuePriority.Low };
            o.Queues["prio-high"] = new SparkQueueOptions { Priority = SparkQueuePriority.High };
            configure?.Invoke(o);
        });
        services.AddScoped<IRecipient<LowWork>, LowRecipient>();
        services.AddScoped<IRecipient<HighWork>, HighRecipient>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_high_priority_message_published_behind_more_than_a_page_of_low_priority_backlog_is_served_first()
    {
        // M16's window could not do this: the first subscription batch is the 256 oldest messages, all
        // Low, and the High message behind them waited for the whole window. The sorted page is the top
        // of the whole claimable set, so the High message is the first one the feeder serves.
        const int lowCount = 300;
        var seen = new Seen();
        await using var provider = BuildHost(seen);
        using (var scope = provider.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            for (var i = 0; i < lowCount; i++)
                await bus.BroadcastAsync(new LowWork(i));
            await bus.BroadcastAsync(new HighWork(1));
        }
        await WaitForIndexesAsync();

        var routed = new ConcurrentQueue<string>();
        provider.GetRequiredService<MessageQueueRouter>().Routed += (queue, _) => routed.Enqueue(queue);
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(() => seen.Low.Count >= lowCount && seen.High.Count >= 1, "the backlog to drain", TimeSpan.FromSeconds(60));

            routed.Should().HaveCount(lowCount + 1);
            routed.First().Should().Be("prio-high", "a High message beats a Low backlog longer than FeederBatchSize (256)");
        }
        finally
        {
            foreach (var service in hosted.AsEnumerable().Reverse())
                await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Low_priority_work_completes_under_sustained_high_priority_traffic()
    {
        // Strict priority, and every page is kept full of High messages: a High backlog several pages
        // deep is published ahead of the Low work, and High traffic keeps arriving from several
        // producers while it drains. A scheduler that served only the sorted page would never reach
        // the Low messages while that lasts; the subscription batch is what reaches them (see
        // MessageFeeder.ServeOrder), so no aging is needed.
        const int pageSize = 16;
        const int highBacklog = pageSize * 8;
        const int lowCount = 60;
        var seen = new Seen();
        await using var provider = BuildHost(seen, o => o.FeederBatchSize = pageSize);

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        // Warm-up: lease, subscription creation. Excluded from the bound below.
        var highSent = 0;
        using (var scope = provider.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.BroadcastAsync(new HighWork(Interlocked.Increment(ref highSent)));
            await AsyncWait.UntilAsync(() => seen.High.Count >= 1, "the warm-up message", TimeSpan.FromSeconds(60));
            for (var i = 0; i < highBacklog; i++)
                await bus.BroadcastAsync(new HighWork(Interlocked.Increment(ref highSent)));
            for (var i = 0; i < lowCount; i++)
                await bus.BroadcastAsync(new LowWork(i));
        }

        using var stop = new CancellationTokenSource();
        var traffic = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var scope = provider.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            while (!stop.IsCancellationRequested)
            {
                await bus.BroadcastAsync(new HighWork(Interlocked.Increment(ref highSent)));
                try { await Task.Delay(5, stop.Token); } catch (OperationCanceledException) { }
            }
        })).ToArray();

        try
        {
            // A failure bound, not a timing assertion.
            await AsyncWait.UntilAsync(() => seen.Low.Count >= lowCount, "every low-priority message, with high-priority traffic running", TimeSpan.FromSeconds(60));
            var highOutstanding = Volatile.Read(ref highSent) - seen.High.Count;
            await stop.CancelAsync();
            await Task.WhenAll(traffic);
            await AsyncWait.UntilAsync(() => seen.High.Count >= highSent, "every high-priority message", TimeSpan.FromSeconds(60));

            seen.Low.Distinct().Count().Should().Be(lowCount);
            seen.High.Distinct().Count().Should().Be(highSent);
            output.WriteLine($"QueuePriority no-starvation: {highSent} High sent, {highOutstanding} still outstanding when the last Low was handled");
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var service in hosted.AsEnumerable().Reverse())
                await service.StopAsync(CancellationToken.None);
        }
    }
}
