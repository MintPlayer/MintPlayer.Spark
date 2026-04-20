using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
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

    public record SuccessMessage(string Id);
    public record FailMessage(string Id);
    public record FatalMessage(string Id);
    public record UnknownTypeMessage(string Id);
    public record MultiHandlerMessage(string Id);

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
            NullLogger<MessageSubscriptionWorker>.Instance);
    }

    private static IServiceProvider ProviderFor<TMessage, TRecipient>(TRecipient instance)
        where TRecipient : class, IRecipient<TMessage>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRecipient<TMessage>>(instance);
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
        // No IRecipient<SuccessMessage> registered — worker should still pick the message up and complete it.
        var sp = new ServiceCollection().BuildServiceProvider();

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
        var sp = new ServiceCollection().BuildServiceProvider();

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
    public async Task Mixed_handlers_one_success_one_NonRetryable_rollup_to_Completed()
    {
        var a = new MultiA();
        var b = new MultiB();
        var services = new ServiceCollection();
        services.AddSingleton<IRecipient<MultiHandlerMessage>>(a);
        services.AddSingleton<IRecipient<MultiHandlerMessage>>(b);
        var sp = services.BuildServiceProvider();

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
