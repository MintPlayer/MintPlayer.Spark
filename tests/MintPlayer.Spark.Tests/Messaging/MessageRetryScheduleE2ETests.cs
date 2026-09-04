using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Retry schedules end to end: each lane waits its <b>own</b> backoff, climbs its own ladder, and
/// dead-letters when the ladder runs out.
/// </summary>
/// <remarks>
/// <para>
/// The intervals are deliberately short (seconds) so these run in a suite, but the <i>shapes</i> are
/// the real ones: an increasing ladder that ends in a dead-letter, and a flat ladder that retries the
/// same interval a fixed number of times. Nothing here asserts elapsed time as a success condition —
/// the assertions are on attempt counts, terminal status, and the order of a recorded log. Timeouts
/// are failure bounds only.
/// </para>
/// <para>
/// What makes these worth having beyond the pure schedule tests: they prove the schedule a lane
/// <i>declares</i> is the one the pump actually waits on, and that two lanes with different schedules
/// do not borrow each other's.
/// </para>
/// </remarks>
public class MessageRetryScheduleE2ETests : SparkTestDriver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(MintPlayer.Spark.Messaging.Indexes.SparkMessages_ByQueue).Assembly];

    public record Doomed(string Key);
    public record Flaky(string Key);

    /// <summary>Always fails, and records when each attempt happened.</summary>
    private sealed class AlwaysFails : IRecipient<Doomed>
    {
        public ConcurrentQueue<DateTime> Attempts { get; } = new();

        public Task HandleAsync(Doomed message, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(DateTime.UtcNow);
            throw new InvalidOperationException($"{message.Key} always fails");
        }
    }

    /// <summary>Fails a fixed number of times, then succeeds.</summary>
    private sealed class FailsNTimes(int failures) : IRecipient<Flaky>
    {
        private int calls;
        public int Calls => calls;

        public Task HandleAsync(Flaky message, CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref calls) <= failures
                ? throw new InvalidOperationException("still failing")
                : Task.CompletedTask;
    }

    [Fact]
    public async Task An_increasing_ladder_is_walked_rung_by_rung_and_then_dead_letters()
    {
        // 1s → 2s → 3s → dead-letter. Four attempts: the ladder's three rungs are each a WAIT after a
        // failure, and the attempt that follows the last rung has nowhere left to go.
        //
        // This is the shape that used to be impossible to express correctly: with a separate
        // MaxAttempts the last rung was unreachable, because dead-lettering fired ON the attempt the
        // final delay was meant to precede. A ladder derives its own limit, so every rung it declares
        // is one it can actually reach.
        var recipient = new AlwaysFails();

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services => services.AddSingleton<IRecipient<Doomed>>(recipient),
            lanes => lanes.Queue("retry-ladder").Concurrent(1).Retry(RetrySchedule.Ladder("1s 2s 3s")));

        var pump = host.StartLane("retry-ladder");
        await host.Bus.BroadcastAsync(new Doomed("d"), "retry-ladder");
        pump.Ring();

        var message = await WaitForAsync("retry-ladder", m => m.Status == EMessageStatus.DeadLettered);

        recipient.Attempts.Should().HaveCount(4, "three rungs allow three retries after the first attempt");
        message.Handlers.Should().ContainSingle().Which.AttemptCount.Should().Be(4);
        message.Handlers[0].Status.Should().Be(EHandlerStatus.DeadLettered);

        // The gaps grew. Asserted as an ordering property of the recorded attempts rather than as
        // wall-clock accuracy: a loaded machine may stretch a gap, but it can never shrink one below
        // the delay the pump was told to wait.
        var attempts = recipient.Attempts.ToList();
        (attempts[2] - attempts[1]).Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(900), "the second rung is 2s and cannot elapse early");
        (attempts[3] - attempts[2]).Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(1900), "the third rung is 3s and cannot elapse early");
    }

    [Fact]
    public async Task A_flat_ladder_retries_the_same_interval_a_fixed_number_of_times()
    {
        // A flat ladder of five rungs → six attempts → dead-letter. A flat ladder is the honest way
        // to say "retry a few times, quickly, then give up", and it is what an operator sets globally
        // in a test environment, so it must behave like any other ladder rather than being special.
        //
        // One second per rung rather than two: the shape is what is under test — flat, five rungs,
        // six attempts — and the interval only decides how long this test holds its database. The
        // suite creates one database per test case and its teardown is the first thing to buckle
        // under load, so a test that waits longer than it needs to makes every other test flakier.
        var recipient = new AlwaysFails();

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services => services.AddSingleton<IRecipient<Doomed>>(recipient),
            lanes => lanes.Queue("retry-flat").Concurrent(1).Retry(RetrySchedule.Ladder("1s 1s 1s 1s 1s")));

        var pump = host.StartLane("retry-flat");
        await host.Bus.BroadcastAsync(new Doomed("f"), "retry-flat");
        pump.Ring();

        var message = await WaitForAsync("retry-flat", m => m.Status == EMessageStatus.DeadLettered);

        recipient.Attempts.Should().HaveCount(6, "five rungs allow five retries after the first attempt");
        message.Handlers[0].Status.Should().Be(EHandlerStatus.DeadLettered);
    }

    [Fact]
    public async Task A_lane_that_declares_no_schedule_uses_the_configured_default()
    {
        // The default is what most lanes get, so it deserves the same end-to-end proof as a declared
        // one. One rung here, to keep the suite quick.
        var recipient = new AlwaysFails();

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services => services.AddSingleton<IRecipient<Doomed>>(recipient),
            lanes => lanes.Queue("retry-default").Concurrent(1),
            options: new SparkMessagingOptions { DefaultRetry = "1s" });

        var pump = host.StartLane("retry-default");
        await host.Bus.BroadcastAsync(new Doomed("x"), "retry-default");
        pump.Ring();

        await WaitForAsync("retry-default", m => m.Status == EMessageStatus.DeadLettered);

        recipient.Attempts.Should().HaveCount(2, "one rung, so one retry");
    }

    [Fact]
    public async Task Two_lanes_wait_their_own_backoff_and_do_not_borrow_each_others()
    {
        // The point of per-lane schedules. A slow lane must not drag a fast one, and a fast lane must
        // not shorten a slow one's ladder — under the old global MaxAttempts/BackoffDelays neither
        // was expressible at all.
        var fast = new FailsNTimes(1);
        var slow = new FailsNTimes(1);

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services =>
            {
                services.AddSingleton<IRecipient<Flaky>>(fast);
                services.AddSingleton<IRecipient<Doomed>>(new AlwaysFails());
            },
            lanes =>
            {
                lanes.Queue("retry-fast").Concurrent(1).Retry(RetrySchedule.Ladder("1s"));
                lanes.Queue("retry-slow").Concurrent(1).Retry(RetrySchedule.Ladder("30s"));
            });

        host.StartLane("retry-fast");
        host.StartLane("retry-slow");

        await host.Bus.BroadcastAsync(new Flaky("fast"), "retry-fast");
        await host.Bus.BroadcastAsync(new Doomed("slow"), "retry-slow");
        host.RingAll();

        // The fast lane finishes its retry while the slow lane is still parked on a 30s rung. This is
        // a state assertion at a causally-defined moment — the fast message reaching Completed — not
        // a race against the clock: the slow lane cannot possibly have retried in that window.
        await WaitForAsync("retry-fast", m => m.Status == EMessageStatus.Completed);

        fast.Calls.Should().Be(2, "the fast lane retried on its own 1s rung");

        using var session = Store.OpenAsyncSession();
        var slowMessage = await session.Query<SparkMessage>()
            .Where(m => m.QueueName == "retry-slow").FirstAsync();

        slowMessage.Status.Should().Be(EMessageStatus.Failed, "the slow lane is parked, not finished");
        slowMessage.Handlers.Should().ContainSingle().Which.AttemptCount.Should().Be(1,
            "a 30s rung cannot have elapsed while a 1s one did");
    }

    [Fact]
    public async Task The_global_override_flattens_every_lane_including_declared_ones()
    {
        // The switch a test environment sets. It must beat a lane's own declaration, or a suite would
        // have to restate every schedule — and it deliberately keeps the ATTEMPT COUNT, so the real
        // dead-letter path is still exercised rather than short-circuited.
        var recipient = new AlwaysFails();

        await using var host = new MessagingTestHost(
            Store,
            TimeProvider.System,
            services => services.AddSingleton<IRecipient<Doomed>>(recipient),
            lanes => lanes.Queue("retry-overridden").Concurrent(1).Retry(RetrySchedule.Ladder("1h 6h 1d")),
            options: new SparkMessagingOptions { RetryOverride = "1s" });

        var pump = host.StartLane("retry-overridden");
        await host.Bus.BroadcastAsync(new Doomed("o"), "retry-overridden");
        pump.Ring();

        // Without the override this lane would take over a day to dead-letter.
        var message = await WaitForAsync("retry-overridden", m => m.Status == EMessageStatus.DeadLettered);

        recipient.Attempts.Should().HaveCount(2, "the override replaces the ladder wholesale");
        message.Handlers[0].Status.Should().Be(EHandlerStatus.DeadLettered);
    }

    private Task<SparkMessage> WaitForAsync(string lane, Func<SparkMessage, bool> predicate)
        => AsyncWait.ForAsync(
            async () =>
            {
                using var session = Store.OpenAsyncSession();
                return await session.Query<SparkMessage>().Where(m => m.QueueName == lane).FirstOrDefaultAsync();
            },
            m => m != null && predicate(m),
            $"a message on '{lane}' to satisfy the predicate",
            last => $"Status={last?.Status}, Attempts=[{string.Join(",", last?.Handlers.Select(h => h.AttemptCount) ?? [])}]",
            PollTimeout,
            TimeSpan.FromMilliseconds(100))!;
}
