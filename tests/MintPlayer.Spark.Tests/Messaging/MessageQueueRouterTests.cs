using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
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
using Raven.Client.Exceptions.Database;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// The lane pump's plumbing around a message: claim renewal while a handler runs, losing the
/// claim, a failing pump step, and a drain that times out. The intervals are options, so no clock
/// seam is needed — they are simply set small.
/// </summary>
public class MessageQueueRouterTests : SparkTestDriver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public sealed class Job { public string? Name { get; set; } }

    /// <summary>Runs <see cref="OnHandle"/> for every message, so each test decides what "handling" does.</summary>
    public sealed class JobRecipient : IRecipient<Job>
    {
        public Func<CancellationToken, Task> OnHandle { get; set; } = _ => Task.CompletedTask;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(Job message, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await OnHandle(cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));

        public bool Has(LogLevel level, string fragment)
            => Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
    }

    private sealed class AllowAll : IMessageTypeAllowList
    {
        public bool IsAllowedMessageType(string? assemblyQualifiedName) => true;
        public bool IsAllowedHandlerType(string? assemblyQualifiedName) => true;
    }

    private static readonly SparkMessagingOptions FastOptions = new()
    {
        ClaimRenewInterval = TimeSpan.FromMilliseconds(20),
        ClaimTtl = TimeSpan.FromMinutes(5),
        HandlerTimeout = TimeSpan.FromMinutes(1),
    };

    private readonly JobRecipient _recipient = new();
    private readonly RecordingLogger<MessageQueueRouter> _log = new();
    private readonly List<IAsyncDisposable> _cleanup = [];

    public override async Task DisposeAsync()
    {
        foreach (var disposable in _cleanup)
            await disposable.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>A store on the same server whose database does not exist: every call through it fails.</summary>
    private IDocumentStore BrokenStore()
    {
        var store = new DocumentStore { Urls = Store.Urls, Database = $"missing-{Guid.NewGuid():N}" }.Initialize();
        _cleanup.Add(new AsyncDisposer(store.Dispose));
        return store;
    }

    private sealed class AsyncDisposer(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private MessageQueueRouter NewRouter(IDocumentStore? processorStore = null, IDocumentStore? routerStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageTypeAllowList>(new AllowAll());
        services.AddSingleton<IRecipient<Job>>(_recipient);
        var provider = services.BuildServiceProvider();
        _cleanup.Add(provider);

        var options = Options.Create(FastOptions);
        var processor = new MessageProcessor(provider, processorStore ?? Store, options, NullLogger<MessageProcessor>.Instance);
        var router = new MessageQueueRouter(processor, routerStore ?? Store, options, _log);
        router.Start(CancellationToken.None);
        _cleanup.Add(router);
        return router;
    }

    private async Task<string> SeedClaimedJobAsync()
    {
        var message = new SparkMessage
        {
            QueueName = "router-tests",
            MessageType = typeof(Job).AssemblyQualifiedName!,
            PayloadJson = JsonConvert.SerializeObject(new Job { Name = "one" }),
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

    private Task<SparkMessage> WaitForStatusAsync(string id, EMessageStatus status)
        => AsyncWait.ForAsync(
            async () =>
            {
                using var session = Store.OpenAsyncSession();
                return await session.LoadAsync<SparkMessage>(id);
            },
            m => m.Status == status,
            $"message {id} to reach {status}",
            m => $"Status={m?.Status}",
            Timeout);

    private async Task RouteAndDrainAsync(MessageQueueRouter router, string id, Func<Task> whileRunning)
    {
        await router.RouteAsync("router-tests", id, CancellationToken.None);
        await whileRunning();
        await router.DrainAsync(Timeout);
    }

    [Fact]
    public async Task A_handler_outliving_the_renew_interval_keeps_its_claim_alive()
    {
        var router = NewRouter();
        var id = await SeedClaimedJobAsync();
        _recipient.OnHandle = _ => Task.Delay(300);

        await RouteAndDrainAsync(router, id, () => WaitForStatusAsync(id, EMessageStatus.Completed));

        // Also covers the race at the end: a renewal that lands after the processor saved Completed
        // fails, and must not be reported as a lost claim.
        _log.Entries.Any(e => e.Level >= LogLevel.Warning).Should().BeFalse(
            "renewing our own claim, many times over, is the normal case. Log:\n{0}",
            string.Join("\n", _log.Entries.Select(e => $"{e.Level}: {e.Message} {e.Exception?.GetType().Name}")));
    }

    /// <summary>
    /// The bug: a renewal that throws something other than a cancellation — a transient RavenDB
    /// error — faulted the renewal task, and the <c>finally</c> that awaited it rethrew. The handler
    /// had succeeded and the message was saved Completed, but the pump reported "Pump failed on
    /// message", and a real processing error in the same position would have been replaced by the
    /// renewal's. It also stopped renewing for the rest of the handler's run after one blip.
    /// </summary>
    [Fact]
    public async Task A_failing_claim_renewal_does_not_mask_a_successful_handler()
    {
        var router = NewRouter(routerStore: BrokenStore());
        var id = await SeedClaimedJobAsync();
        _recipient.OnHandle = _ => Task.Delay(300);

        await RouteAndDrainAsync(router, id, () => WaitForStatusAsync(id, EMessageStatus.Completed));

        _log.Has(LogLevel.Error, "Pump for queue").Should().BeFalse(
            "the message was processed successfully; a renewal hiccup is not a pump failure. Log:\n{0}",
            string.Join("\n", _log.Entries.Select(e => $"{e.Level}: {e.Message} {e.Exception?.GetType().Name}")));
        _log.Entries.Any(e => e.Level == LogLevel.Warning && e.Exception is DatabaseDoesNotExistException).Should().BeTrue(
            "the failed renewal is still reported, as the warning it is");
    }

    [Fact]
    public async Task Losing_the_claim_mid_handler_is_reported()
    {
        var router = NewRouter();
        var id = await SeedClaimedJobAsync();
        _recipient.OnHandle = async _ =>
        {
            // Another host's sweeper reclaimed the message while this handler was running.
            using (var session = Store.OpenAsyncSession())
            {
                var message = await session.LoadAsync<SparkMessage>(id);
                message.OwnerId = "other-node/0002";
                await session.SaveChangesAsync();
            }

            await AsyncWait.UntilAsync(() => _log.Has(LogLevel.Warning, "Lost the claim"), "the renewal to notice", Timeout);
        };

        await RouteAndDrainAsync(router, id, () => _recipient.Started.Task.WaitAsync(Timeout));

        _log.Has(LogLevel.Warning, "Lost the claim").Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_outside_the_handlers_is_logged_and_the_pump_carries_on()
    {
        // The processor cannot even load the message, so ProcessAsync throws out of the pump step.
        var router = NewRouter(processorStore: BrokenStore());
        var id = await SeedClaimedJobAsync();

        await router.RouteAsync("router-tests", id, CancellationToken.None);
        await AsyncWait.UntilAsync(() => _log.Has(LogLevel.Error, "Pump for queue"), "the pump to log the failure", Timeout);
        await router.RouteAsync("router-tests", id, CancellationToken.None);
        await AsyncWait.UntilAsync(
            () => _log.Entries.Count(e => e.Level == LogLevel.Error) >= 2,
            "the pump to survive and take the next message", Timeout);

        await router.DrainAsync(Timeout);
        router.LaneCount.Should().Be(1);
    }

    [Fact]
    public async Task A_drain_that_times_out_says_so()
    {
        var router = NewRouter();
        var id = await SeedClaimedJobAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recipient.OnHandle = _ => release.Task;

        await router.RouteAsync("router-tests", id, CancellationToken.None);
        await _recipient.Started.Task.WaitAsync(Timeout);
        await router.DrainAsync(TimeSpan.FromMilliseconds(50));

        _log.Has(LogLevel.Warning, "did not drain").Should().BeTrue();

        release.SetResult();
        await WaitForStatusAsync(id, EMessageStatus.Completed);
    }

    [Fact]
    public async Task Draining_a_router_with_no_lanes_returns_at_once()
    {
        var router = NewRouter();

        await router.DrainAsync(Timeout);

        router.StartedLaneCount.Should().Be(0);
    }
}
