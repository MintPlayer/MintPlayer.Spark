using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// End-to-end tests for <see cref="MessageSubscriptionWorker"/> driving real RavenDB
/// subscriptions via <see cref="SparkTestDriver"/>. Each test seeds a <see cref="SparkMessage"/>,
/// starts the worker, and polls for the resulting terminal document state.
/// <para>
/// Workers are intentionally constructed directly (bypassing <see cref="MessageSubscriptionManager"/>)
/// so tests stay focused on a single queue. <c>MaxDocsPerBatch = 1</c> on the worker means
/// each message transitions independently.
/// </para>
/// </summary>
public class MessageSubscriptionWorkerE2ETests : SparkTestDriver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);

    // MessageRetrySweeper queries the SparkMessages_ByQueue index (the redelivery tests
    // depend on it); production deploys it via AddMessaging's middleware.
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    public record SuccessMessage(string Id);
    public record FailMessage(string Id);
    public record FatalMessage(string Id);
    public record UnknownTypeMessage(string Id);
    public record MultiHandlerMessage(string Id);
    public record RedeliveryMultiMessage(string Id);

    // --- Recipients -----------------------------------------------------------

    public sealed class SuccessRecipient : IRecipient<SuccessMessage>
    {
        public List<string> Received { get; } = new();

        public Task HandleAsync(SuccessMessage message, CancellationToken cancellationToken = default)
        {
            Received.Add(message.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class AlwaysFailsRecipient : IRecipient<FailMessage>
    {
        public int Calls { get; private set; }

        public Task HandleAsync(FailMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }

    public sealed class EventuallySucceedsRecipient : IRecipient<FailMessage>
    {
        public int Calls { get; private set; }

        public Task HandleAsync(FailMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                throw new InvalidOperationException("transient boom");
            return Task.CompletedTask;
        }
    }

    public sealed class MultiCountingSuccessRecipient : IRecipient<RedeliveryMultiMessage>
    {
        public int Calls { get; private set; }

        public Task HandleAsync(RedeliveryMultiMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    public sealed class MultiEventuallySucceedsRecipient : IRecipient<RedeliveryMultiMessage>
    {
        public int Calls { get; private set; }

        public Task HandleAsync(RedeliveryMultiMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                throw new InvalidOperationException("transient boom");
            return Task.CompletedTask;
        }
    }

    public sealed class NonRetryableRecipient : IRecipient<FatalMessage>
    {
        public Task HandleAsync(FatalMessage message, CancellationToken cancellationToken = default)
            => throw new NonRetryableException("cannot process");
    }

    public sealed class MultiA : IRecipient<MultiHandlerMessage>
    {
        public int Calls { get; private set; }

        public Task HandleAsync(MultiHandlerMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    public sealed class MultiB : IRecipient<MultiHandlerMessage>
    {
        public Task HandleAsync(MultiHandlerMessage message, CancellationToken cancellationToken = default)
            => throw new NonRetryableException("B is broken");
    }

    // --- Helpers --------------------------------------------------------------

    private async Task<string> SeedAsync<T>(T payload, string? queueNameOverride = null, int maxAttempts = 5)
    {
        var bus = new MessageBus(Store, Options.Create(new SparkMessagingOptions { MaxAttempts = maxAttempts }));
        if (queueNameOverride == null)
            await bus.BroadcastAsync(payload);
        else
            await bus.BroadcastAsync(payload, queueNameOverride);

        WaitForIndexing(Store);
        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkMessage>().SingleAsync();
        return stored.Id!;
    }

    private async Task<SparkMessage> WaitForMessageAsync(string id, Func<SparkMessage, bool> predicate, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? PollTimeout);
        SparkMessage? last = null;
        while (DateTime.UtcNow < end)
        {
            using var session = Store.OpenAsyncSession();
            last = await session.LoadAsync<SparkMessage>(id);
            if (last != null && predicate(last))
                return last;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Predicate for SparkMessage '{id}' not met within {timeout ?? PollTimeout}. Last: Status={last?.Status}, Handlers=[{string.Join(",", last?.Handlers.Select(h => $"{h.Status}:{h.AttemptCount}") ?? [])}]");
    }

    private MessageSubscriptionWorker NewWorker(
        string queueName,
        IServiceProvider serviceProvider,
        SparkMessagingOptions? options = null)
    {
        return new MessageSubscriptionWorker(
            queueName,
            Store,
            serviceProvider,
            Options.Create(options ?? new SparkMessagingOptions { MaxAttempts = 5 }),
            NullLoggerFactory.Instance);
    }

    /// <summary>
    /// The wake-up mechanism for parked messages. Redelivery tests must run one alongside
    /// the worker: the embedded test server has no document refresh enabled, so without the
    /// sweeper a Failed/delayed message is never re-evaluated (the exact issue #233 bug).
    /// </summary>
    private MessageRetrySweeper NewSweeper(SparkMessagingOptions? options = null)
    {
        return new MessageRetrySweeper(
            Store,
            Options.Create(options ?? new SparkMessagingOptions { FallbackPollInterval = TimeSpan.FromSeconds(1) }),
            NullLogger<MessageRetrySweeper>.Instance);
    }

    private static IServiceProvider ProviderFor<TMessage, TRecipient>(TRecipient instance)
        where TRecipient : class, IRecipient<TMessage>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRecipient<TMessage>>(instance);
        // R2-H6: the worker now requires IMessageTypeAllowList to gate Type.GetType.
        // The allow-list reads its set from registered IRecipient<T> services via
        // IServiceCollectionAccessor, so wire both with the same ServiceCollection.
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        return services.BuildServiceProvider();
    }

    private static IServiceProvider EmptyProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        return services.BuildServiceProvider();
    }

    private static IServiceProvider ProviderForMulti<TMessage>(params IRecipient<TMessage>[] recipients)
    {
        var services = new ServiceCollection();
        foreach (var r in recipients)
            services.AddSingleton(r);
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        return services.BuildServiceProvider();
    }

    // --- Tests ----------------------------------------------------------------

    [Fact]
    public async Task Happy_path_single_recipient_transitions_message_to_Completed()
    {
        var recipient = new SuccessRecipient();
        var sp = ProviderFor<SuccessMessage, SuccessRecipient>(recipient);

        var id = await SeedAsync(new SuccessMessage("orders/1"));
        var worker = NewWorker(typeof(SuccessMessage).FullName!, sp);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Completed);

            final.Handlers.Should().ContainSingle();
            final.Handlers[0].Status.Should().Be(EHandlerStatus.Completed);
            final.Handlers[0].CompletedAtUtc.Should().NotBeNull();
            final.CompletedAtUtc.Should().NotBeNull();
            recipient.Received.Should().Equal("orders/1");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Message_without_recipients_rollups_to_Completed_with_empty_Handlers()
    {
        // No IRecipient<SuccessMessage> registered. R2-H6: the allow-list is
        // computed from registered IRecipient<T>s and SuccessMessage isn't on
        // it, so the message dead-letters before Type.GetType. We register
        // SuccessMessage's recipient interface as a no-op stub so the allow-list
        // accepts the type — keeping this test's existing "no recipients in the
        // resolved scope → empty Handlers + Completed" contract.
        var stubRecipient = Substitute.For<IRecipient<SuccessMessage>>();
        var services = new ServiceCollection();
        // NOT registering as a service so resolution returns empty — but we DO
        // need the allow-list to include the type. Use the message-type-only
        // wiring trick: add the type via a typed Func<,> sentinel that's never resolved.
        // Simpler: register and remove. Actually the cleanest path is to register
        // the recipient interface but not give the worker a way to resolve actual
        // instances — that's what GetServices returns on the existing path.
        // For test stability, just have the empty-handlers case re-route through
        // a transient stub that returns empty enumeration.
        services.AddSingleton<IServiceCollectionAccessor>(_ =>
        {
            var inner = new ServiceCollection();
            inner.AddSingleton<IRecipient<SuccessMessage>>(stubRecipient);
            return new ServiceCollectionAccessor(inner);
        });
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        var sp = services.BuildServiceProvider();

        var id = await SeedAsync(new SuccessMessage("orders/empty"));
        var worker = NewWorker(typeof(SuccessMessage).FullName!, sp);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Completed);
            final.Handlers.Should().BeEmpty();
            final.CompletedAtUtc.Should().NotBeNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Handler_throwing_NonRetryableException_is_dead_lettered_on_first_attempt()
    {
        var sp = ProviderFor<FatalMessage, NonRetryableRecipient>(new NonRetryableRecipient());

        var id = await SeedAsync(new FatalMessage("orders/fatal"));
        var worker = NewWorker(typeof(FatalMessage).FullName!, sp);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.DeadLettered);

            final.Handlers.Should().ContainSingle();
            final.Handlers[0].Status.Should().Be(EHandlerStatus.DeadLettered);
            final.Handlers[0].LastError.Should().Be("cannot process");
            // NonRetryable path doesn't increment AttemptCount
            final.Handlers[0].AttemptCount.Should().Be(0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Retryable_handler_failure_with_MaxAttempts_1_dead_letters_the_handler_and_message()
    {
        var recipient = new AlwaysFailsRecipient();
        var sp = ProviderFor<FailMessage, AlwaysFailsRecipient>(recipient);

        var id = await SeedAsync(new FailMessage("orders/retry"), maxAttempts: 1);
        var worker = NewWorker(typeof(FailMessage).FullName!, sp);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.DeadLettered);

            final.Handlers.Should().ContainSingle();
            final.Handlers[0].Status.Should().Be(EHandlerStatus.DeadLettered);
            final.Handlers[0].AttemptCount.Should().Be(1);
            final.Handlers[0].LastError.Should().Be("boom");
            recipient.Calls.Should().Be(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Retryable_handler_failure_within_MaxAttempts_leaves_handler_Failed_and_message_Failed_with_NextAttempt()
    {
        var recipient = new AlwaysFailsRecipient();
        var sp = ProviderFor<FailMessage, AlwaysFailsRecipient>(recipient);

        // MaxAttempts high so the first pickup stays in Failed (not DeadLettered)
        var id = await SeedAsync(new FailMessage("orders/soft-fail"), maxAttempts: 5);
        var options = new SparkMessagingOptions
        {
            MaxAttempts = 5,
            BackoffDelays = [TimeSpan.FromMinutes(1)], // deterministic, but we don't wait for it
        };
        var worker = NewWorker(typeof(FailMessage).FullName!, sp, options);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Failed);

            final.Handlers.Should().ContainSingle();
            final.Handlers[0].Status.Should().Be(EHandlerStatus.Failed);
            final.Handlers[0].AttemptCount.Should().Be(1);
            final.NextAttemptAtUtc.Should().NotBeNull();
            final.NextAttemptAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow + TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
            // CompletedAtUtc stays null while still retrying
            final.CompletedAtUtc.Should().BeNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unresolvable_MessageType_is_DeadLettered_without_invoking_handlers()
    {
        // Empty allow-list: no IRecipient registrations → R2-H6 dead-letters
        // the message before Type.GetType. Same observable outcome as the
        // original test (Type.GetType returning null), via the new gate.
        var sp = EmptyProvider();

        // Manually insert a SparkMessage whose MessageType cannot be resolved by Type.GetType
        var queueName = "ghost-queue";
        string id;
        using (var session = Store.OpenAsyncSession())
        {
            var msg = new SparkMessage
            {
                QueueName = queueName,
                MessageType = "Nope.Does.Not.Exist, GhostAssembly",
                PayloadJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                Status = EMessageStatus.Pending,
                MaxAttempts = 3,
            };
            await session.StoreAsync(msg);
            await session.SaveChangesAsync();
            id = msg.Id!;
        }
        WaitForIndexing(Store);

        var worker = NewWorker(queueName, sp);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.DeadLettered);
            final.Handlers.Should().BeEmpty();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_message_is_redelivered_after_backoff_and_completes()
    {
        // Issue #233: the retry state machine parks a message at Failed +
        // NextAttemptAtUtc, but redelivery needs an active wake-up — the
        // subscription only re-evaluates a document when it is written.
        var recipient = new EventuallySucceedsRecipient();
        var sp = ProviderFor<FailMessage, EventuallySucceedsRecipient>(recipient);

        var id = await SeedAsync(new FailMessage("orders/transient"), maxAttempts: 5);

        var options = new SparkMessagingOptions
        {
            MaxAttempts = 5,
            BackoffDelays = [TimeSpan.FromSeconds(1)], // keep the test fast
            FallbackPollInterval = TimeSpan.FromSeconds(1),
        };
        var worker = NewWorker(typeof(FailMessage).FullName!, sp, options);
        var sweeper = NewSweeper(options);

        await worker.StartAsync(CancellationToken.None);
        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Completed);

            recipient.Calls.Should().Be(2);
            final.Handlers.Should().ContainSingle();
            final.Handlers[0].Status.Should().Be(EHandlerStatus.Completed);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DelayBroadcast_message_is_picked_up_after_the_delay()
    {
        // Issue #233 corollary: a delayed message is evaluated once at creation
        // (Pending, but NextAttemptAtUtc in the future -> no match) and needs a
        // wake-up when the delay elapses.
        var recipient = new SuccessRecipient();
        var sp = ProviderFor<SuccessMessage, SuccessRecipient>(recipient);

        var bus = new MessageBus(Store, Options.Create(new SparkMessagingOptions()));
        await bus.DelayBroadcastAsync(new SuccessMessage("orders/delayed"), TimeSpan.FromSeconds(1));
        WaitForIndexing(Store);
        string id;
        using (var session = Store.OpenAsyncSession())
            id = (await session.Query<SparkMessage>().SingleAsync()).Id!;

        var worker = NewWorker(typeof(SuccessMessage).FullName!, sp);
        var sweeper = NewSweeper();

        await worker.StartAsync(CancellationToken.None);
        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Completed);
            recipient.Received.Should().Equal("orders/delayed");
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Redelivery_skips_already_completed_handlers()
    {
        // Per-handler semantics across a real second delivery: the handler that completed
        // on attempt 1 must not run again when the message is redelivered for the one that
        // failed transiently.
        var completing = new MultiCountingSuccessRecipient();
        var eventually = new MultiEventuallySucceedsRecipient();
        var sp = ProviderForMulti<RedeliveryMultiMessage>(completing, eventually);

        var id = await SeedAsync(new RedeliveryMultiMessage("orders/multi-transient"));

        var options = new SparkMessagingOptions
        {
            MaxAttempts = 5,
            BackoffDelays = [TimeSpan.FromSeconds(1)],
            FallbackPollInterval = TimeSpan.FromSeconds(1),
        };
        var worker = NewWorker(typeof(RedeliveryMultiMessage).FullName!, sp, options);
        var sweeper = NewSweeper(options);

        await worker.StartAsync(CancellationToken.None);
        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Completed);

            completing.Calls.Should().Be(1, "a handler that completed on the first delivery must be skipped on redelivery");
            eventually.Calls.Should().Be(2);
            final.Handlers.Should().HaveCount(2);
            final.Handlers.Should().OnlyContain(h => h.Status == EHandlerStatus.Completed);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sweeper_touches_only_due_parked_messages()
    {
        var now = DateTime.UtcNow;
        string dueFailedId, futureFailedId, completedId, duePendingId;
        using (var session = Store.OpenAsyncSession())
        {
            SparkMessage NewMessage(EMessageStatus status, DateTime? nextAttempt) => new()
            {
                QueueName = "sweeper-queue",
                MessageType = typeof(SuccessMessage).AssemblyQualifiedName!,
                PayloadJson = "{}",
                CreatedAtUtc = now,
                Status = status,
                NextAttemptAtUtc = nextAttempt,
                MaxAttempts = 3,
            };

            var dueFailed = NewMessage(EMessageStatus.Failed, now.AddSeconds(-5));
            var futureFailed = NewMessage(EMessageStatus.Failed, now.AddHours(1));
            var completed = NewMessage(EMessageStatus.Completed, now.AddSeconds(-5));
            var duePending = NewMessage(EMessageStatus.Pending, now.AddSeconds(-5));

            await session.StoreAsync(dueFailed);
            await session.StoreAsync(futureFailed);
            await session.StoreAsync(completed);
            await session.StoreAsync(duePending);
            await session.SaveChangesAsync();

            (dueFailedId, futureFailedId, completedId, duePendingId) =
                (dueFailed.Id!, futureFailed.Id!, completed.Id!, duePending.Id!);
        }
        WaitForIndexing(Store);

        var touched = await NewSweeper().SweepOnceAsync(CancellationToken.None);
        touched.Should().Be(2);

        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<SparkMessage>(dueFailedId)).WakeUp.Should().BeTrue();
        (await verify.LoadAsync<SparkMessage>(duePendingId)).WakeUp.Should().BeTrue();
        (await verify.LoadAsync<SparkMessage>(futureFailedId)).WakeUp.Should().BeFalse();
        (await verify.LoadAsync<SparkMessage>(completedId)).WakeUp.Should().BeFalse();
    }

    [Fact]
    public async Task Mixed_handlers_one_success_one_NonRetryable_rollup_to_Completed()
    {
        var a = new MultiA();
        var b = new MultiB();
        var sp = ProviderForMulti<MultiHandlerMessage>(a, b);

        var id = await SeedAsync(new MultiHandlerMessage("orders/mixed"));
        var worker = NewWorker(typeof(MultiHandlerMessage).FullName!, sp);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // allTerminal=true (one Completed + one DeadLettered), allDeadLettered=false → Completed
            var final = await WaitForMessageAsync(id, m => m.Status == EMessageStatus.Completed);

            final.Handlers.Should().HaveCount(2);
            final.Handlers.Should().Contain(h => h.Status == EHandlerStatus.Completed);
            final.Handlers.Should().Contain(h => h.Status == EHandlerStatus.DeadLettered);
            a.Calls.Should().Be(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
