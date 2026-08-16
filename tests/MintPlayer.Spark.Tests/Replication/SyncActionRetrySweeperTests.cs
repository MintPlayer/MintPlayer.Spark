using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Indexes;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;

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
    public async Task Wakes_an_action_parked_before_WakeUp_existed()
    {
        // The upgrade case. Any deployment running the broken query has actions already parked with
        // a NextAttemptAtUtc and no WakeUp property in their JSON at all — the field did not exist
        // when they were written. If a missing field does not satisfy the sweeper's filter, those
        // actions stay stranded forever and the fix rescues nothing that is already broken.
        var legacy = Action(ESyncActionStatus.Pending, DateTime.UtcNow.AddMinutes(-5));
        await SeedAsync(session => session.StoreAsync(legacy));

        // Remove the property to reproduce a pre-#258 document exactly, rather than relying on a
        // default-valued field being equivalent to an absent one — which is the whole question.
        var patch = await Store.Operations.SendAsync(new PatchByQueryOperation(
            $"from SparkSyncActions where id() = '{legacy.Id}' update {{ delete this.WakeUp; }}"));
        await patch.WaitForCompletionAsync();
        await WaitForIndexesAsync();

        using (var check = Store.OpenAsyncSession())
        {
            // A projection always includes the key it was asked for, so absence shows up as a null
            // value rather than a missing entry. That still distinguishes the two cases: a stored
            // `false` projects as false, not null.
            var raw = await check.Advanced.AsyncRawQuery<Dictionary<string, object>>(
                $"from SparkSyncActions where id() = '{legacy.Id}' select WakeUp").ToListAsync();
            raw.Should().ContainSingle().Which["WakeUp"].Should().BeNull(
                "the probe is only meaningful if the property really is absent — a stored false "
                + "would come back as false");
        }

        var woken = await NewSweeper().SweepOnceAsync(CancellationToken.None);

        woken.Should().Be(1,
            "an action parked by the previous version must still be found, or upgrading fixes only "
            + "future failures and leaves the existing backlog dead");

        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<SparkSyncAction>(legacy.Id!)).WakeUp.Should().BeTrue();
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
