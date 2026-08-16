using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Indexes;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Tests the component that makes a retry backoff observable to a subscription (#258).
/// <para>
/// A subscription is only re-evaluated when a document is written, so nothing about an elapsed
/// backoff can be expressed in the query itself. <see cref="SyncActionRetrySweeper"/> is what
/// evaluates the clock and writes the answer down; without it, every scheduled retry is a retry that
/// never happens.
/// </para>
/// </summary>
public class SyncActionRetrySweeperTests : SparkTestDriver
{
    protected override IEnumerable<Assembly> IndexAssemblies => [typeof(SparkSyncActions_ByStatus).Assembly];

    private SyncActionRetrySweeper NewSweeper()
        => new(
            Store,
            Options.Create(new SparkReplicationOptions
            {
                ModuleName = "HR",
                ModuleUrl = "http://hr.test",
                FallbackPollInterval = TimeSpan.FromSeconds(1),
            }),
            NullLogger<SyncActionRetrySweeper>.Instance);

    private static SparkSyncAction Action(ESyncActionStatus status, DateTime? nextAttempt, bool wakeUp = false)
        => new()
        {
            OwnerModuleName = "Fleet",
            RequestingModule = "HR",
            Collection = "Cars",
            Actions = [new SyncAction
            {
                ActionType = SyncActionType.Insert,
                Collection = "Cars",
                Data = new Dictionary<string, object?> { ["Plate"] = "ABC-123" },
            }],
            Status = status,
            NextAttemptAtUtc = nextAttempt,
            WakeUp = wakeUp,
        };

    [Fact]
    public async Task Wakes_only_pending_actions_whose_backoff_has_elapsed()
    {
        var now = DateTime.UtcNow;

        var due = Action(ESyncActionStatus.Pending, now.AddMinutes(-1));
        var notYetDue = Action(ESyncActionStatus.Pending, now.AddMinutes(30));
        var brandNew = Action(ESyncActionStatus.Pending, nextAttempt: null);
        var alreadyWoken = Action(ESyncActionStatus.Pending, now.AddMinutes(-1), wakeUp: true);
        var terminallyFailed = Action(ESyncActionStatus.Failed, now.AddMinutes(-1));
        var completed = Action(ESyncActionStatus.Completed, nextAttempt: null);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(due);
            await session.StoreAsync(notYetDue);
            await session.StoreAsync(brandNew);
            await session.StoreAsync(alreadyWoken);
            await session.StoreAsync(terminallyFailed);
            await session.StoreAsync(completed);
        });

        var woken = await NewSweeper().SweepOnceAsync(CancellationToken.None);

        woken.Should().Be(1, "only one action is Pending, overdue, and not already woken");

        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<SparkSyncAction>(due.Id!)).WakeUp.Should().BeTrue();
        (await verify.LoadAsync<SparkSyncAction>(due.Id!)).LastWakeUpUtc.Should().NotBeNull();

        (await verify.LoadAsync<SparkSyncAction>(notYetDue.Id!)).WakeUp.Should().BeFalse(
            "its backoff has not elapsed — waking it would defeat the backoff entirely");
        (await verify.LoadAsync<SparkSyncAction>(brandNew.Id!)).WakeUp.Should().BeFalse(
            "a new action needs no wake-up: the subscription already received it on the write that created it");
        (await verify.LoadAsync<SparkSyncAction>(terminallyFailed.Id!)).WakeUp.Should().BeFalse(
            "Failed is terminal for replication — reviving it here would change the retry contract");
        (await verify.LoadAsync<SparkSyncAction>(completed.Id!)).WakeUp.Should().BeFalse();
    }

    [Fact]
    public async Task Sweeping_is_idempotent_so_a_woken_action_is_not_repatched_every_interval()
    {
        // The sweeper runs on a timer. If it rewrote WakeUp on every pass it would bump the change
        // vector each time and hand the subscription the same action repeatedly, turning one retry
        // into a redelivery loop for as long as the action stayed Pending.
        var due = Action(ESyncActionStatus.Pending, DateTime.UtcNow.AddMinutes(-1));
        await SeedAsync(session => session.StoreAsync(due));

        var sweeper = NewSweeper();

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        await WaitForIndexesAsync();

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0,
            "the action is already awake; the worker is what clears the gate on pickup");
    }
}
