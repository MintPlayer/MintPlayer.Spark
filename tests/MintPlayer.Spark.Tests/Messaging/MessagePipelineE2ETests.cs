using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// The handler contract, end to end: what a message's status becomes, per handler, in every outcome.
/// </summary>
/// <remarks>
/// Replaces the per-queue subscription worker's suite. The behaviour under test is unchanged — these
/// are the same cases — but they now run through <see cref="MintPlayer.Spark.Messaging.Services.MessageLanePump"/>
/// and <see cref="MintPlayer.Spark.Messaging.Services.MessageProcessor"/>, because a subscription per
/// queue no longer exists.
/// </remarks>
public class MessagePipelineE2ETests : SparkTestDriver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private const string Lane = "pipeline-e2e";

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    public record Payload(string Id);

    private sealed class Succeeds : IRecipient<Payload>
    {
        public int Calls;
        public Task HandleAsync(Payload message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFails : IRecipient<Payload>
    {
        public int Calls;
        public Task HandleAsync(Payload message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("always fails");
        }
    }

    private sealed class Fatal : IRecipient<Payload>
    {
        public int Calls;
        public Task HandleAsync(Payload message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new NonRetryableException("cannot ever work");
        }
    }

    [Fact]
    public async Task Happy_path_completes_the_message()
    {
        var recipient = new Succeeds();
        await using var host = NewHost(services => services.AddSingleton<IRecipient<Payload>>(recipient));

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new Payload("ok"), Lane);
        pump.Ring();

        var message = await WaitForAsync(m => m.Status == EMessageStatus.Completed);

        recipient.Calls.Should().Be(1);
        message.Handlers.Should().ContainSingle().Which.Status.Should().Be(EHandlerStatus.Completed);
        message.CompletedAtUtc.Should().HaveValue();
    }

    [Fact]
    public async Task A_message_nobody_handles_is_completed_not_dead_lettered()
    {
        // Publishing to zero subscribers is a successful publish, not a failure. Dead-letter is kept
        // for "we tried and failed" or "we refuse", so that a dead-letter view stays worth reading —
        // a framework lane such as spark-github-all broadcasts typed messages most applications never
        // subscribe to, and dead-lettering those would bury real faults.
        //
        // Terminal is the property that fixes the leak: only terminal paths stamp @expires, so a
        // message left Pending would accumulate forever. Production still carries documents of
        // exactly this shape.
        //
        // The type is still never resolved — the allow-list gate runs before Type.GetType, which is
        // what stops a writer of SparkMessages choosing what gets instantiated. The gate decides
        // whether we touch the type, not what status we record.
        await using var host = NewHost(_ => { });

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new Payload("orphan"), Lane);
        pump.Ring();

        var message = await WaitForAsync(m => m.Status == EMessageStatus.Completed);
        message.Handlers.Should().BeEmpty();
        message.CompletedAtUtc.Should().HaveValue("a terminal message must be stamped for retention");
    }

    [Fact]
    public async Task A_non_retryable_failure_dead_letters_that_handler_immediately()
    {
        var fatal = new Fatal();
        await using var host = NewHost(services => services.AddSingleton<IRecipient<Payload>>(fatal));

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new Payload("fatal"), Lane);
        pump.Ring();

        var message = await WaitForAsync(m => m.Status == EMessageStatus.DeadLettered);

        fatal.Calls.Should().Be(1, "a non-retryable failure must not be attempted twice");
        message.Handlers.Should().ContainSingle().Which.Status.Should().Be(EHandlerStatus.DeadLettered);
    }

    [Fact]
    public async Task A_handler_that_keeps_failing_is_dead_lettered_when_the_schedule_gives_up()
    {
        var failing = new AlwaysFails();
        await using var host = NewHost(
            services => services.AddSingleton<IRecipient<Payload>>(failing),
            // One rung: attempt 1 waits 1s, attempt 2 has nowhere to go and dead-letters.
            RetrySchedule.Ladder("1s"));

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new Payload("doomed"), Lane);
        pump.Ring();

        var message = await WaitForAsync(m => m.Status == EMessageStatus.DeadLettered);

        failing.Calls.Should().Be(2, "the ladder allows one retry before giving up");
        message.Handlers.Should().ContainSingle().Which.Status.Should().Be(EHandlerStatus.DeadLettered);
    }

    [Fact]
    public async Task One_failing_handler_does_not_prevent_a_sibling_from_completing()
    {
        var ok = new Succeeds();
        var fatal = new Fatal();

        await using var host = NewHost(services =>
        {
            services.AddSingleton<IRecipient<Payload>>(ok);
            services.AddSingleton<IRecipient<Payload>>(fatal);
        });

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new Payload("mixed"), Lane);
        pump.Ring();

        // Not all dead-lettered, so the message completes rather than dead-lettering.
        var message = await WaitForAsync(m => m.Status == EMessageStatus.Completed);

        message.Handlers.Should().HaveCount(2);
        message.Handlers.Should().ContainSingle(h => h.Status == EHandlerStatus.Completed);
        message.Handlers.Should().ContainSingle(h => h.Status == EHandlerStatus.DeadLettered);
    }

    [Fact]
    public async Task A_failing_message_is_retried_after_its_backoff_and_then_completes()
    {
        var flaky = new FailsThenSucceeds();
        await using var host = NewHost(
            services => services.AddSingleton<IRecipient<Payload>>(flaky),
            RetrySchedule.Ladder("1s"));

        var pump = host.StartLane(Lane);
        await host.Bus.BroadcastAsync(new Payload("transient"), Lane);
        pump.Ring();

        var message = await WaitForAsync(m => m.Status == EMessageStatus.Completed);

        flaky.Calls.Should().Be(2);
        message.Handlers.Should().ContainSingle().Which.AttemptCount.Should().Be(1,
            "the attempt counter records the failure, not the success that followed");
    }

    [Fact]
    public async Task A_delayed_broadcast_is_not_run_before_it_becomes_visible()
    {
        var recipient = new Succeeds();
        await using var host = NewHost(services => services.AddSingleton<IRecipient<Payload>>(recipient));

        var pump = host.StartLane(Lane);
        await host.Bus.DelayBroadcastAsync(new Payload("later"), TimeSpan.FromSeconds(2), Lane);
        pump.Ring();

        // Positive signal: the document exists and carries the visibility stamp.
        var stored = await WaitForAsync(m => m.VisibleAtUtc != null);
        stored.Status.Should().Be(EMessageStatus.Pending);
        recipient.Calls.Should().Be(0, "the delay has not elapsed at the moment the document appears");

        // Then it runs on its own, without anything re-broadcasting it — the drain's own idle pass
        // rediscovers it, which is what replaced the retry sweeper.
        await WaitForAsync(m => m.Status == EMessageStatus.Completed, TimeSpan.FromSeconds(40));
        recipient.Calls.Should().Be(1);
    }

    private sealed class FailsThenSucceeds : IRecipient<Payload>
    {
        public int Calls;
        public Task HandleAsync(Payload message, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Calls) == 1)
                throw new InvalidOperationException("transient");

            return Task.CompletedTask;
        }
    }

    private MessagingTestHost NewHost(Action<IServiceCollection> recipients, IRetrySchedule? schedule = null)
        => new(
            Store,
            TimeProvider.System,
            recipients,
            lanes => lanes.Queue(Lane).Concurrent(maxConcurrency: 1).Retry(schedule ?? RetrySchedule.Ladder("1s")));

    private Task<SparkMessage> WaitForAsync(Func<SparkMessage, bool> predicate, TimeSpan? timeout = null)
        => AsyncWait.ForAsync(
            async () =>
            {
                using var session = Store.OpenAsyncSession();
                return await session.Query<SparkMessage>().Where(m => m.QueueName == Lane).FirstOrDefaultAsync();
            },
            m => m != null && predicate(m),
            "the message to satisfy the predicate",
            last => $"Status={last?.Status}, Handlers=[{string.Join(",", last?.Handlers.Select(h => $"{h.Status}:{h.AttemptCount}") ?? [])}]",
            timeout ?? PollTimeout,
            TimeSpan.FromMilliseconds(100))!;
}
