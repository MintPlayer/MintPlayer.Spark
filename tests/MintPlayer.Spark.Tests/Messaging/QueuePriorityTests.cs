using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// #460 M16: priority lanes in the single feeder — strict priority inside a look-ahead window, FIFO
/// per queue, every message of a window served in that window (the no-starvation bound), the admission
/// decision taken before the claim, and configuration beating code for <c>Priority</c>.
/// </summary>
public class QueuePriorityTests : SparkTestDriver
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    private static SparkMessage M(string id, string queue) => new() { Id = id, QueueName = queue };

    [Fact]
    public void A_window_is_served_highest_priority_first_in_delivery_order_within_each_priority()
    {
        var priorities = new Dictionary<string, SparkQueuePriority> { ["urgent"] = SparkQueuePriority.High, ["bulk"] = SparkQueuePriority.Low };
        SparkQueuePriority Of(string q) => priorities.GetValueOrDefault(q, SparkQueuePriority.Normal);
        var window = new[] { M("b1", "bulk"), M("n1", "plain"), M("b2", "bulk"), M("u1", "urgent"), M("n2", "plain"), M("u2", "urgent"), new SparkMessage { QueueName = "bulk" } };

        var groups = MessageFeeder.PriorityWindow(window, Of);

        groups.Select(g => string.Join(",", g.Select(m => m.Id))).Should().Equal(["u1,u2", "n1,n2", "b1,b2"], "the id-less document is skipped");
    }

    [Fact]
    public void Every_message_of_a_window_is_served_in_that_window_whatever_the_mix()
    {
        // The no-starvation bound: a window is a permutation of what was delivered, so a low-priority
        // message is overtaken by at most the window's other messages and never carried into the next.
        var random = new Random(460);
        var queues = new[] { "a", "b", "c" };
        var levels = new Dictionary<string, SparkQueuePriority> { ["a"] = SparkQueuePriority.High, ["b"] = SparkQueuePriority.Normal, ["c"] = SparkQueuePriority.Low };
        var window = Enumerable.Range(0, 256).Select(i => M($"m{i}", queues[random.Next(3)])).ToList();

        var served = MessageFeeder.PriorityWindow(window, q => levels[q]).SelectMany(g => g).ToList();

        served.Select(m => m.Id).Order().Should().Equal(window.Select(m => m.Id).Order());
        foreach (var queue in queues)
            served.Where(m => m.QueueName == queue).Select(m => m.Id).Should().Equal(window.Where(m => m.QueueName == queue).Select(m => m.Id), "FIFO within a queue");
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

    [Fact]
    public async Task Low_priority_work_completes_under_sustained_high_priority_traffic()
    {
        var seen = new Seen();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSingleton(seen);
        services.AddSparkMessaging(o =>
        {
            o.FeederBatchSize = 16;
            o.FallbackPollInterval = TimeSpan.FromSeconds(1);
            o.Queues["prio-low"] = new SparkQueueOptions { Priority = SparkQueuePriority.Low };
            o.Queues["prio-high"] = new SparkQueueOptions { Priority = SparkQueuePriority.High };
        });
        services.AddScoped<IRecipient<LowWork>, LowRecipient>();
        services.AddScoped<IRecipient<HighWork>, HighRecipient>();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        // Warm-up: lease, subscription creation. Excluded from the bound below.
        const int lowCount = 60;
        using (var scope = provider.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.BroadcastAsync(new HighWork(0));
            await AsyncWait.UntilAsync(() => seen.High.Count >= 1, "the warm-up message", TimeSpan.FromSeconds(60));
            for (var i = 0; i < lowCount; i++)
                await bus.BroadcastAsync(new LowWork(i));
        }
        using var stop = new CancellationTokenSource();
        var highSent = 0;
        var traffic = Task.Run(async () =>
        {
            using var scope = provider.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            while (!stop.IsCancellationRequested)
            {
                await bus.BroadcastAsync(new HighWork(Interlocked.Increment(ref highSent)));
                try { await Task.Delay(10, stop.Token); } catch (OperationCanceledException) { }
            }
        });

        try
        {
            // A failure bound, not a timing assertion: without the window bound, a feeder serving
            // high priority first across windows would never reach the low backlog while traffic lasts.
            await AsyncWait.UntilAsync(() => seen.Low.Count >= lowCount, "every low-priority message, with high-priority traffic running", TimeSpan.FromSeconds(60));
            await stop.CancelAsync();
            await traffic;
            await AsyncWait.UntilAsync(() => seen.High.Count >= highSent, "every high-priority message", TimeSpan.FromSeconds(30));

            seen.Low.Distinct().Count().Should().Be(lowCount);
            highSent.Should().BeGreaterThan(0);
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var service in hosted.AsEnumerable().Reverse())
                await service.StopAsync(CancellationToken.None);
        }
    }
}
