using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Pins <see cref="MessageSubscriptionManager"/>'s start-up lifecycle in both subscription modes.
/// <para>
/// This class previously asserted that the manager "starts a worker per discovered queue" and that
/// a subscription named after the queue appears on the server. That premise is exactly what the
/// single-subscription rework deletes, so the fact was rewritten rather than adapted: in the
/// default mode the assertion is now the opposite one — <b>one</b> subscription exists, it is named
/// <c>SparkMessaging</c>, and no per-queue definition is created however many queues are
/// discovered.
/// </para>
/// </summary>
public class MessageSubscriptionManagerLifecycleTests : SparkTestDriver
{
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    [Fact]
    public async Task Logs_a_warning_and_returns_when_no_IRecipient_is_registered()
    {
        // No IRecipient<T> registered → DiscoverQueueNames yields an empty set →
        // ExecuteAsync hits the early-return branch.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);

        await cts.CancelAsync();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SingleSubscription_mode_creates_exactly_one_subscription_named_SparkMessaging()
    {
        var provider = BuildProvider(ESubscriptionMode.SingleSubscription,
            registerSecondQueue: true);
        await using var _ = provider;

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            // Wait on the observable signal — the subscription existing on the server — rather
            // than sleeping and hoping. A fixed delay here used to make this pass whether or not
            // anything was ever created.
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128)
                    .Any(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant),
                $"the manager to create the shared '{MessageFeeder.SubscriptionNameConstant}' subscription",
                TimeSpan.FromSeconds(10));

            var all = Store.Subscriptions.GetSubscriptions(0, 128);

            // The point of the rework: two queues, still one subscription.
            all.Should().ContainSingle(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant);
            all.Where(s => s.SubscriptionName?.StartsWith("SparkMessaging-", StringComparison.Ordinal) == true)
                .Should().BeEmpty("no per-queue definition may be created in SingleSubscription mode");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SubscriptionPerQueue_mode_still_creates_one_subscription_per_queue()
    {
        var provider = BuildProvider(ESubscriptionMode.SubscriptionPerQueue,
            registerSecondQueue: true);
        await using var _ = provider;

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128)
                    .Count(s => s.SubscriptionName?.StartsWith("SparkMessaging-", StringComparison.Ordinal) == true) >= 2,
                "the manager to create a per-queue subscription for both queues",
                TimeSpan.FromSeconds(20));

            var perQueue = Store.Subscriptions.GetSubscriptions(0, 128)
                .Where(s => s.SubscriptionName?.StartsWith("SparkMessaging-", StringComparison.Ordinal) == true)
                .Select(s => s.SubscriptionName!)
                .ToList();

            perQueue.Should().Contain($"SparkMessaging-{FirstQueue}");
            perQueue.Should().Contain($"SparkMessaging-{SecondQueue}");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Legacy_per_queue_definitions_are_pruned_on_startup_and_the_shared_one_survives()
    {
        // Two stale definitions of the shape the old design created, plus a name that must NOT be
        // touched. The prefix guard is one character wide — "SparkMessaging" versus
        // "SparkMessaging-" — so this asserts the boundary directly.
        await Store.Subscriptions.CreateAsync(new Raven.Client.Documents.Subscriptions.SubscriptionCreationOptions
        {
            Name = "SparkMessaging-StaleQueueOne",
            Query = "from SparkMessages"
        });
        await Store.Subscriptions.CreateAsync(new Raven.Client.Documents.Subscriptions.SubscriptionCreationOptions
        {
            Name = "SparkMessaging-StaleQueueTwo",
            Query = "from SparkMessages"
        });

        var provider = BuildProvider(ESubscriptionMode.SingleSubscription, registerSecondQueue: false);
        await using var _ = provider;

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await AsyncWait.UntilAsync(
                () => Store.Subscriptions.GetSubscriptions(0, 128)
                    .Any(s => s.SubscriptionName == MessageFeeder.SubscriptionNameConstant),
                "the shared subscription to be created after the prune",
                TimeSpan.FromSeconds(10));

            var names = Store.Subscriptions.GetSubscriptions(0, 128)
                .Select(s => s.SubscriptionName!)
                .ToList();

            names.Should().NotContain("SparkMessaging-StaleQueueOne");
            names.Should().NotContain("SparkMessaging-StaleQueueTwo");
            names.Should().Contain(MessageFeeder.SubscriptionNameConstant,
                "the shared subscription has no trailing hyphen, so the legacy prefix must not match it");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_graceful_stop_releases_the_messaging_lease()
    {
        // What a graceful handover rests on (spike S-M8, #460 M16b): without the release a standby
        // waits out the whole 30 s TTL instead of taking over on its next poll. The lease is a
        // compare-exchange value; after a graceful stop it must be gone.
        var provider = BuildProvider(ESubscriptionMode.SingleSubscription, registerSecondQueue: false);
        await using var _ = provider;
        var hosted = provider.GetServices<IHostedService>().OfType<MessageSubscriptionManager>().Single();

        await hosted.StartAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => hosted.IsLeader, "the manager to take the lease", TimeSpan.FromSeconds(30));
        await hosted.StopAsync(CancellationToken.None);

        var lease = await Store.Operations.SendAsync(
            new Raven.Client.Documents.Operations.CompareExchange.GetCompareExchangeValueOperation<MessagingLease>("spark/messaging/leader"));
        lease.Should().BeNull("a graceful stop releases the lease so a standby takes over on its next poll");
    }

    [Fact]
    public async Task A_graceful_stop_with_an_already_cancelled_token_still_releases_the_lease()
    {
        // A host's stop token can be cancelled before StopAsync runs (its shutdown timeout already
        // spent). The release must not ride on it, or every standby waits out the 30 s TTL.
        var provider = BuildProvider(ESubscriptionMode.SingleSubscription, registerSecondQueue: false);
        await using var _ = provider;
        var hosted = provider.GetServices<IHostedService>().OfType<MessageSubscriptionManager>().Single();

        await hosted.StartAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => hosted.IsLeader, "the manager to take the lease", TimeSpan.FromSeconds(30));
        await hosted.StopAsync(new CancellationToken(canceled: true));

        (await ReadLeaseAsync()).Should().BeNull("the release runs on its own bounded token, not the cancelled stop token");
    }

    [Fact]
    public async Task A_renewal_falling_due_during_the_drain_does_not_restart_messaging()
    {
        // Spike S-M8 (#460 M16b). The lease loop used to run on the host's stopping token alone,
        // which fires only in base.StopAsync, after the drain. A renewal falling due during the drain
        // saw isLeader == false (StopAsync had cleared it), re-acquired its own lease and started a
        // second feeder on a host that was stopping. A handler held open makes the drain outlast
        // RenewInterval, so the renewal is certain to fall due inside it.
        var gate = new HoldGate();
        var log = new System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new RecordingLoggerProvider(log)));
        services.AddSingleton(Store);
        services.AddSingleton(gate);
        services.AddSparkMessaging(o => o.ClaimTtl = TimeSpan.FromMinutes(2));
        services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestHold>, TestHoldRecipient>();
        var provider = services.BuildServiceProvider();
        await using var _ = provider;
        var hosted = provider.GetServices<IHostedService>().OfType<MessageSubscriptionManager>().Single();

        await hosted.StartAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => hosted.IsLeader, "the manager to take the lease", TimeSpan.FromSeconds(30));
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>().BroadcastAsync(new TestHold());
        // A failure bound, not a wait: delivery takes milliseconds, but a feeder whose first connect
        // fails on a starved runner retries after 30 s, so the bound must clear one retry.
        try { await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException)
        {
            throw new TimeoutException("The held message was never handled. Warnings: "
                + string.Join(" | ", log.Where(e => e.Level >= LogLevel.Warning).Select(e => $"{e.Category}: {e.Message}")));
        }

        var original = hosted.Feeder;
        original.Should().NotBeNull();
        var stop = hosted.StopAsync(CancellationToken.None);
        try
        {
            // Hold the drain open until one of the two outcomes is observable: the lease loop has
            // ended (fixed), or its renewal fell due inside the drain and started a feeder that is
            // not the one being drained (the bug). Not IsLeader: that flag says nothing about which
            // of the two happened (CI read it before StopAsync had cleared it, #460 M16b).
            await AsyncWait.UntilAsync(
                () => hosted.ExecuteTask!.IsCompleted || hosted.Feeder is { } f && !ReferenceEquals(f, original),
                "the lease loop to end, or to restart messaging behind the drain", MessagingLeaseManager.RenewInterval * 3);
        }
        finally
        {
            gate.Release.TrySetResult();
            await stop;
        }

        hosted.IsLeader.Should().BeFalse("a stopping host must not re-acquire its lease and restart messaging");
        hosted.Feeder.Should().BeNull("no feeder may survive the stop");
        (await ReadLeaseAsync()).Should().BeNull("the stop still releases the lease");
    }

    [Fact]
    public async Task A_stop_after_the_store_is_disposed_logs_no_lease_error_and_leaves_the_lease_to_lapse()
    {
        // Spike S-M8's crashed host (#460 M16b): its document store was disposed, and its teardown
        // logged "Error releasing the messaging lease -- OperationCanceledException" (RavenDB's
        // context pool throws that on a disposed store). A release that cannot run is expected - the
        // lease lapses at its TTL, the path a standby handles for any crash - not an error.
        var store = new Raven.Client.Documents.DocumentStore { Urls = Store.Urls, Database = Store.Database };
        store.ApplySparkConventions();
        store.Initialize();

        var log = new System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message)>();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new RecordingLoggerProvider(log)));
        services.AddSingleton<Raven.Client.Documents.IDocumentStore>(store);
        services.AddSparkMessaging(o => o.SubscriptionMode = ESubscriptionMode.SingleSubscription);
        services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>, TestPingRecipient>();
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().OfType<MessageSubscriptionManager>().Single();

        await hosted.StartAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => hosted.IsLeader, "the manager to take the lease", TimeSpan.FromSeconds(30));
        store.Dispose();
        await hosted.StopAsync(CancellationToken.None);
        try { await provider.DisposeAsync(); } catch (ObjectDisposedException) { }

        log.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("lease", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("a release against a disposed store is expected, not an error");
        var lease = await ReadLeaseAsync();
        lease.Should().NotBeNull("nothing could release it; it lapses at its TTL");
        lease!.NodeId.Should().Be(MessageClaims.NodeId);
    }

    /// <summary>
    /// Q-M11e (docs/seo_analyzer_PRD.md §12). A <c>WebApplicationFactory</c> host is stopped twice at once: the
    /// factory's <c>DisposeAsync</c> calls <c>Host.StopAsync</c>, which raises ApplicationStopping, and the app's own
    /// <c>app.Run()</c> wakes on it and calls <c>Host.StopAsync</c> too; <c>RunAsync</c>'s <c>finally</c> then
    /// disposes the host (its logger providers, the Windows EventLog among them) as soon as its own stop returns. Each
    /// call ran its own drain on its own <c>ClaimTtl</c> timer, so whichever finished second logged "did not drain"
    /// into a disposed EventLog and threw <c>ObjectDisposedException</c> out of the other stop. The pumps cancelled at
    /// the end of a stop also logged after it had returned. Here the second stop starts a little later, so the order
    /// is fixed, and the sink is "disposed" the moment the first stop returns, exactly as <c>RunAsync</c> does.
    /// </summary>
    [Fact]
    public async Task A_second_concurrent_stop_does_no_work_after_the_first_one_returns()
    {
        var gate = new HoldGate();
        var sink = new DisposableLogSink();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(sink));
        services.AddSingleton(Store);
        services.AddSingleton(gate);
        services.AddSparkMessaging(o =>
        {
            // The drain waits ClaimTtl; renewals keep the held message's claim alive meanwhile.
            o.ClaimTtl = TimeSpan.FromSeconds(1);
            o.ClaimRenewInterval = TimeSpan.FromMilliseconds(200);
        });
        services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestHold>, TestCancellableHoldRecipient>();
        var provider = services.BuildServiceProvider();
        await using var _ = provider;
        var hosted = provider.GetServices<IHostedService>().OfType<MessageSubscriptionManager>().Single();

        await hosted.StartAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => hosted.IsLeader, "the manager to take the lease", TimeSpan.FromSeconds(30));
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>().BroadcastAsync(new TestHold());
        // A failure bound, not a wait (see A_renewal_falling_due_during_the_drain_does_not_restart_messaging).
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(90));

        var factoryStop = hosted.StopAsync(CancellationToken.None);
        await Task.Delay(300);
        var runStop = hosted.StopAsync(CancellationToken.None);

        var first = await Task.WhenAny(factoryStop, runStop);
        sink.Dispose(); // RunAsync's finally: the host and its logger providers go the moment its own stop returns.
        await first;

        var second = first == factoryStop ? runStop : factoryStop;
        var act = () => second.WaitAsync(TimeSpan.FromSeconds(30));
        await act.Should().NotThrowAsync("the second stop must not log into a host its twin already disposed");
        sink.LateWrites.Should().BeEmpty("no messaging work may outlive a StopAsync call");
    }

    private async Task<MessagingLease?> ReadLeaseAsync()
        => (await Store.Operations.SendAsync(
            new Raven.Client.Documents.Operations.CompareExchange.GetCompareExchangeValueOperation<MessagingLease>("spark/messaging/leader")))?.Value;

    private ServiceProvider BuildProvider(ESubscriptionMode mode, bool registerSecondQueue)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging(o => o.SubscriptionMode = mode);
        services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>, TestPingRecipient>();
        if (registerSecondQueue)
            services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPong>, TestPongRecipient>();
        return services.BuildServiceProvider();
    }

    private const string FirstQueue = "MessageSubscriptionManagerLifecycleTests-Ping";
    private const string SecondQueue = "MessageSubscriptionManagerLifecycleTests-Pong";

    [MintPlayer.Spark.Messaging.Abstractions.MessageQueueAttribute(FirstQueue)]
    public sealed class TestPing
    {
        public string? Hello { get; set; }
    }

    [MintPlayer.Spark.Messaging.Abstractions.MessageQueueAttribute(SecondQueue)]
    public sealed class TestPong
    {
        public string? Hello { get; set; }
    }

    private sealed class TestPingRecipient : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>
    {
        public Task HandleAsync(TestPing message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestPongRecipient : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPong>
    {
        public Task HandleAsync(TestPong message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string HoldQueue = "MessageSubscriptionManagerLifecycleTests-Hold";

    [MintPlayer.Spark.Messaging.Abstractions.MessageQueueAttribute(HoldQueue)]
    public sealed class TestHold
    {
        public string? Hello { get; set; }
    }

    private sealed class HoldGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Holds the drain open until the test lets go. Not bound to the handler's token: the drain must wait for it.</summary>
    private sealed class TestHoldRecipient(HoldGate gate) : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestHold>
    {
        public async Task HandleAsync(TestHold message, CancellationToken cancellationToken)
        {
            gate.Started.TrySetResult();
            await gate.Release.Task;
        }
    }

    /// <summary>
    /// Holds the handler until the stop cancels it, so the drain times out; then takes half a second to unwind, as a
    /// cancelled HTTP request or database write does. The second stop's drain falls inside that half second.
    /// </summary>
    private sealed class TestCancellableHoldRecipient(HoldGate gate) : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestHold>
    {
        public async Task HandleAsync(TestHold message, CancellationToken cancellationToken)
        {
            gate.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
                throw;
            }
        }
    }

    /// <summary>
    /// Behaves like the Windows EventLog provider after its host is disposed: a write throws
    /// <see cref="ObjectDisposedException"/>. Late writes are also recorded, because a pump that logs after the
    /// stop throws on a thread nobody observes.
    /// </summary>
    private sealed class DisposableLogSink : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private volatile bool disposed;

        public System.Collections.Concurrent.ConcurrentQueue<string> LateWrites { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        /// <summary>Not the container's disposal: the test calls it when the first stop returns.</summary>
        public void Dispose() => disposed = true;

        private sealed class Logger(DisposableLogSink sink, string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!sink.disposed)
                    return;
                sink.LateWrites.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
                throw new ObjectDisposedException("EventLog");
            }
        }
    }

    private sealed class RecordingLoggerProvider(
        System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message)> entries)
        : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(entries, categoryName);
        public void Dispose() { }

        private sealed class Logger(
            System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message)> entries,
            string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, category, formatter(state, exception)));
        }
    }
}
