using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Spike <b>S-M8</b> (#460, M16b): a leader handover under load. Two messaging hosts share one database,
/// each with its own <see cref="IDocumentStore"/> and its own identity (<see cref="MessageClaims.HostScope"/>),
/// with a 1,000-message mixed-priority backlog. Two handovers are measured:
/// <list type="number">
/// <item><b>Crash</b> — the leader's document store is disposed mid-drain, so it releases nothing: no
/// lease release, no drain, its claims left at <c>Processing</c>. The standby takes over when the lease
/// lapses (<see cref="MessagingLeaseManager.Ttl"/>) and its sweeper reclaims the orphaned claims once
/// <see cref="SparkMessagingOptions.ClaimTtl"/> has passed.</item>
/// <item><b>Graceful</b> — the new leader is stopped with <c>StopAsync</c> while a third host stands by:
/// feeding stops, the lanes drain, the lease is released.</item>
/// </list>
/// In both, a High message is published during the handover and must be served ahead of every lower
/// priority message the new leader serves. Opt-in (<c>SPARK_SPIKE_SM8=1</c>): the crash waits out the
/// 30 s lease. Asserts no loss, no duplicate on the graceful path, and at most one duplicate per lane
/// (a handler that finished on the crashed host but could not record it) on the crash path; prints the gaps.
/// </summary>
public class LeaderHandoverSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private const string High = "sm8-high";
    private const string Normal = "sm8-normal";
    private const string Low = "sm8-low";
    private static readonly TimeSpan HandlerDelay = TimeSpan.FromMilliseconds(3);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(SparkMessages_ByPriority).Assembly];

    [MessageQueue(High)] public sealed record HighJob(int N);
    [MessageQueue(Normal)] public sealed record NormalJob(int N);
    [MessageQueue(Low)] public sealed record LowJob(int N);

    public sealed class Ledger
    {
        public ConcurrentDictionary<string, int> Handled { get; } = new();
        public ConcurrentDictionary<string, ConcurrentBag<string>> HandledBy { get; } = new();
        public int Distinct => Handled.Count;

        public async Task RecordAsync(string key, CancellationToken cancellationToken)
        {
            // The side effect comes last, as in a real handler: a host that dies after it but before the
            // processor records completion is exactly the at-least-once window being measured.
            await Task.Delay(HandlerDelay, cancellationToken);
            Handled.AddOrUpdate(key, 1, (_, n) => n + 1);
            HandledBy.GetOrAdd(key, _ => []).Add(MessageClaims.NodeId);
        }
    }

    public sealed class HighRecipient(Ledger ledger) : IRecipient<HighJob>
    { public Task HandleAsync(HighJob m, CancellationToken ct = default) => ledger.RecordAsync($"h{m.N}", ct); }
    public sealed class NormalRecipient(Ledger ledger) : IRecipient<NormalJob>
    { public Task HandleAsync(NormalJob m, CancellationToken ct = default) => ledger.RecordAsync($"n{m.N}", ct); }
    public sealed class LowRecipient(Ledger ledger) : IRecipient<LowJob>
    { public Task HandleAsync(LowJob m, CancellationToken ct = default) => ledger.RecordAsync($"l{m.N}", ct); }

    private sealed class Host(string name, IDocumentStore store, ServiceProvider provider) : IAsyncDisposable
    {
        public string Name { get; } = name;
        public IDocumentStore Store { get; } = store;
        public ServiceProvider Provider { get; } = provider;
        public List<IHostedService> Services { get; } = provider.GetServices<IHostedService>().ToList();
        public ConcurrentQueue<(string Queue, string Id, DateTime At)> Routed { get; } = new();
        public MessageSubscriptionManager Manager => Services.OfType<MessageSubscriptionManager>().Single();

        public async Task StopAsync()
        {
            // Under the host's own identity: the lease release is guarded on the holder's NodeId, and
            // stopping under the test's identity would (correctly) leave this host's lease in place.
            MessageClaims.HostScope.Value = Name;
            foreach (var service in Services.AsEnumerable().Reverse())
            {
                try { await service.StopAsync(CancellationToken.None); }
                catch (Exception) { /* the crashed host's store is gone; its services fail to stop cleanly */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Store.Dispose();
        }
    }

    private Host CreateHost(string name, Ledger ledger)
    {
        var store = new DocumentStore { Urls = Store.Urls, Database = Store.Database };
        store.ApplySparkConventions();
        store.Initialize();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new TestOutputLoggerProvider(output)));
        services.AddSingleton<IDocumentStore>(store);
        services.AddSingleton(ledger);
        services.AddSparkMessaging(o =>
        {
            o.FallbackPollInterval = TimeSpan.FromSeconds(1);
            o.ClaimTtl = TimeSpan.FromSeconds(10);
            o.ClaimRenewInterval = TimeSpan.FromSeconds(3);
            o.Queues[High] = new SparkQueueOptions { Priority = SparkQueuePriority.High };
            o.Queues[Normal] = new SparkQueueOptions { Priority = SparkQueuePriority.Normal };
            o.Queues[Low] = new SparkQueueOptions { Priority = SparkQueuePriority.Low };
        });
        services.AddScoped<IRecipient<HighJob>, HighRecipient>();
        services.AddScoped<IRecipient<NormalJob>, NormalRecipient>();
        services.AddScoped<IRecipient<LowJob>, LowRecipient>();

        var host = new Host(name, store, services.BuildServiceProvider());
        host.Provider.GetRequiredService<MessageQueueRouter>().Routed += (queue, id) => host.Routed.Enqueue((queue, id, DateTime.UtcNow));
        return host;
    }

    /// <summary>Starts a host under its own identity: every task its services start inherits the scope.</summary>
    private static async Task StartAsync(Host host)
    {
        MessageClaims.HostScope.Value = host.Name;
        foreach (var service in host.Services)
            await service.StartAsync(CancellationToken.None);
    }

    private static async Task PublishMixedAsync(Host host, int from, int count)
    {
        using var scope = host.Provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        for (var i = from; i < from + count; i++)
        {
            switch (i % 3)
            {
                case 0: await bus.BroadcastAsync(new HighJob(i)); break;
                case 1: await bus.BroadcastAsync(new NormalJob(i)); break;
                default: await bus.BroadcastAsync(new LowJob(i)); break;
            }
        }
    }

    private MessagingLeaseManager LeaseHolder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSingleton<MessagingLeaseManager>();
        return services.BuildServiceProvider().GetRequiredService<MessagingLeaseManager>();
    }

    private static async Task<T> WithScopeAsync<T>(string identity, Func<Task<T>> action)
    {
        MessageClaims.HostScope.Value = identity;
        return await action();
    }

    /// <summary>
    /// Publishes <paramref name="count"/> mixed messages and then one High message in a single
    /// transaction, shaped exactly as <c>MessageBus</c> writes them (priority stamped).
    /// </summary>
    private static async Task PublishAtomicallyAsync(Host host, int from, int count, int highLast)
    {
        using var session = host.Store.OpenAsyncSession();
        async Task StoreAsync(object payload, string queue, SparkQueuePriority priority)
            => await session.StoreAsync(new SparkMessage
            {
                QueueName = queue,
                MessageType = payload.GetType().AssemblyQualifiedName!,
                PayloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(payload),
                CreatedAtUtc = DateTime.UtcNow,
                Priority = (int)priority,
                MaxAttempts = 5,
                Status = EMessageStatus.Pending,
            });

        for (var i = from; i < from + count; i++)
        {
            switch (i % 3)
            {
                case 0: await StoreAsync(new HighJob(i), High, SparkQueuePriority.High); break;
                case 1: await StoreAsync(new NormalJob(i), Normal, SparkQueuePriority.Normal); break;
                default: await StoreAsync(new LowJob(i), Low, SparkQueuePriority.Low); break;
            }
        }
        await StoreAsync(new HighJob(highLast), High, SparkQueuePriority.High);
        await session.SaveChangesAsync();
    }

    private static string KeyOf(int i) => (i % 3) switch { 0 => $"h{i}", 1 => $"n{i}", _ => $"l{i}" };

    [Fact]
    public async Task S_M8_leader_handover_under_load()
    {
        if (Environment.GetEnvironmentVariable("SPARK_SPIKE_SM8") != "1")
        {
            output.WriteLine("S-M8 skipped: set SPARK_SPIKE_SM8=1 (the crash handover waits out the 30 s lease).");
            return;
        }

        const int backlog = 1000;
        var ledger = new Ledger();
        var a = CreateHost("host-A", ledger);
        var b = CreateHost("host-B", ledger);
        Host? c = null;
        try
        {
            await PublishMixedAsync(a, 0, backlog);
            await WaitForIndexesAsync();

            await StartAsync(a);
            await AsyncWait.UntilAsync(() => a.Manager.IsLeader, "host A to lead", TimeSpan.FromSeconds(30));
            await StartAsync(b);

            // ---- 1. crash mid-drain -----------------------------------------------------------------
            await AsyncWait.UntilAsync(() => ledger.Distinct >= 250, "host A to be mid-drain", TimeSpan.FromSeconds(60));
            var crashedAt = DateTime.UtcNow;
            var handledAtCrash = ledger.Distinct;
            a.Store.Dispose();

            using (var scope = b.Provider.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IMessageBus>().BroadcastAsync(new HighJob(-1));

            await AsyncWait.UntilAsync(() => b.Manager.IsLeader, "host B to take over", MessagingLeaseManager.Ttl + TimeSpan.FromSeconds(60));
            var bLeadsAt = DateTime.UtcNow;
            await AsyncWait.UntilAsync(() => ledger.Distinct >= backlog + 1, "every message after the crash", TimeSpan.FromSeconds(120));
            var crashDrained = DateTime.UtcNow;

            var crashFirstServed = b.Routed.First().At;
            var crashDuplicates = ledger.Handled.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
            var crashHighId = await IdOfAsync(High, -1);
            var crashOrder = b.Routed.Select(r => r).ToList();
            var crashHighAt = crashOrder.FindIndex(r => r.Id == crashHighId);

            output.WriteLine($"S-M8 crash: {handledAtCrash} of {backlog} handled when host A's store was disposed; "
                + $"B leader after {(bLeadsAt - crashedAt).TotalSeconds:F1} s (lease TTL {MessagingLeaseManager.Ttl.TotalSeconds:F0} s, standby poll {MessagingLeaseManager.StandbyPollInterval.TotalSeconds:F0} s), "
                + $"B's first message served after {(crashFirstServed - crashedAt).TotalSeconds:F1} s, all {backlog + 1} handled after {(crashDrained - crashedAt).TotalSeconds:F1} s");
            output.WriteLine($"S-M8 crash: lost={backlog + 1 - ledger.Distinct} duplicates={crashDuplicates.Count} [{string.Join(", ", crashDuplicates)}]; "
                + $"B served {crashOrder.Count} (by queue: {string.Join(", ", crashOrder.GroupBy(r => r.Queue).Select(g => $"{g.Key}={g.Count()}"))}); "
                + $"the High message published during the handover was B's #{crashHighAt + 1}, after {crashOrder.Take(crashHighAt).Count(r => r.Queue != High)} lower-priority ones");

            Enumerable.Range(0, backlog).Select(KeyOf).Append("h-1").Except(ledger.Handled.Keys).Should().BeEmpty("no message is lost");
            crashDuplicates.Count.Should().BeLessThanOrEqualTo(3, "at most the handler in flight on each of the crashed host's three lanes runs twice");
            crashHighAt.Should().BeGreaterThanOrEqualTo(0);
            crashOrder.Take(crashHighAt).Should().OnlyContain(r => r.Queue == High, "the new leader serves the High message ahead of every lower priority");

            // ---- 2. graceful handover --------------------------------------------------------------
            // B drains a second backlog, is stopped gracefully mid-drain, and while nobody leads a third
            // backlog plus a High message is committed in ONE transaction, so the new leader's first
            // page already sees all of it and the order assertion cannot race its first wake-up.
            c = CreateHost("host-C", ledger);
            await StartAsync(c);
            await PublishMixedAsync(b, backlog, backlog);
            await AsyncWait.UntilAsync(() => ledger.Distinct >= backlog + 1 + 250, "host B to be mid-drain", TimeSpan.FromSeconds(60));

            var stopRequestedAt = DateTime.UtcNow;
            await b.StopAsync();
            var bStoppedAt = DateTime.UtcNow;

            // Hold the released lease for the test, so the backlog is committed AND indexed while
            // nobody feeds; C's takeover gap is then measured from the test's release.
            var holder = LeaseHolder();
            var held = await WithScopeAsync("spike-holder", () => holder.TryAcquireAsync(CancellationToken.None));
            const int handedOverCount = 300;
            await PublishAtomicallyAsync(c, 2 * backlog, handedOverCount, highLast: -2);
            await WaitForIndexesAsync();
            var releasedAt = DateTime.UtcNow;
            if (held)
                await WithScopeAsync("spike-holder", async () => { await holder.ReleaseAsync(CancellationToken.None); return true; });

            await AsyncWait.UntilAsync(() => c.Manager.IsLeader, "host C to take over", MessagingLeaseManager.Ttl + TimeSpan.FromSeconds(60));
            var cLeadsAt = DateTime.UtcNow;
            await AsyncWait.UntilAsync(() => ledger.Distinct >= 2 * backlog + 2 + handedOverCount, "every message after the graceful handover", TimeSpan.FromSeconds(120));

            var gracefulKeys = Enumerable.Range(backlog, backlog + handedOverCount).Select(KeyOf).Append("h-2").ToHashSet();
            var gracefulDuplicates = ledger.Handled.Where(kv => gracefulKeys.Contains(kv.Key) && kv.Value > 1).Select(kv => kv.Key).ToList();
            var gracefulHighId = await IdOfAsync(High, -2);
            var gracefulOrder = c.Routed.ToList();
            var gracefulHighAt = gracefulOrder.FindIndex(r => r.Id == gracefulHighId);

            output.WriteLine($"S-M8 graceful: B's StopAsync took {(bStoppedAt - stopRequestedAt).TotalSeconds:F1} s (feeder stop + lane drain + lease release); "
                + $"test held the lease for the {handedOverCount} + 1 High commit: {held}; "
                + $"C leader {(cLeadsAt - releasedAt).TotalSeconds:F1} s after the lease was released (standby poll {MessagingLeaseManager.StandbyPollInterval.TotalSeconds:F0} s); C served {gracefulOrder.Count}; "
                + $"lost={gracefulKeys.Count(k => !ledger.Handled.ContainsKey(k))} duplicates={gracefulDuplicates.Count}; "
                + $"the High message was C's #{gracefulHighAt + 1}, after {gracefulOrder.Take(Math.Max(0, gracefulHighAt)).Count(r => r.Queue != High)} lower-priority ones");

            gracefulKeys.Except(ledger.Handled.Keys).Should().BeEmpty("no message is lost");
            gracefulDuplicates.Should().BeEmpty("a graceful handover drains before it lets go");
            gracefulHighAt.Should().BeGreaterThanOrEqualTo(0, "the High message published while B stopped is served by C");
            if (held)
                gracefulOrder.Take(gracefulHighAt).Should().OnlyContain(r => r.Queue == High, "the new leader's first page is the sorted backlog");
            else
                output.WriteLine("S-M8 graceful: C took the lease before the test could hold it; the order check is inconclusive and skipped");
        }
        finally
        {
            foreach (var host in new[] { c, b, a }.OfType<Host>())
            {
                await host.StopAsync();
                try { await host.DisposeAsync(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task<string> IdOfAsync(string queue, int n)
    {
        using var session = Store.OpenAsyncSession();
        var payload = $"{{\"N\":{n}}}";
        var message = await session.Query<SparkMessage>().Customize(x => x.WaitForNonStaleResults()).Where(m => m.QueueName == queue && m.PayloadJson == payload).FirstAsync();
        return message.Id!;
    }
}
