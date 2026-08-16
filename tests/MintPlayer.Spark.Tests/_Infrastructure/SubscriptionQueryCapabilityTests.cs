using System.Collections.Concurrent;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Demonstrates how RavenDB subscription filtering actually behaves, because Spark's replication and
/// messaging retry paths are both built on it and a wrong mental model here is invisible in
/// production: a subscription that matches nothing looks exactly like a queue with nothing in it.
/// <para>
/// The single fact everything else follows from: <b>a subscription is change-vector-driven.</b> A
/// document is tested against the query when it is <i>written</i>, and at no other time. Time passing
/// is not a write.
/// </para>
/// <para>
/// This is why <c>NextAttemptAtUtc &lt;= now()</c> — the obvious way to express "redeliver once the
/// backoff elapses" — cannot work in a subscription, and why both workers instead use a boolean gate
/// that a background sweeper sets (<c>SparkSyncAction.WakeUp</c>, <c>SparkMessage.WakeUp</c>). See
/// #258 and #233.
/// </para>
/// </summary>
public class SubscriptionQueryCapabilityTests : SparkTestDriver
{
    /// <summary>Mirrors the fields of <c>SparkSyncAction</c> that the real subscription query reads.</summary>
    public class Widget
    {
        public string? Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? NextAttemptAtUtc { get; set; }
        public bool WakeUp { get; set; }
    }

    /// <summary>
    /// Runs a subscription over <paramref name="query"/> until <paramref name="until"/> is satisfied,
    /// and returns the ids delivered. Throws if it never is.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> DeliveredByAsync(
        string query,
        Func<IReadOnlyCollection<string>, bool> until,
        string description,
        Func<Task>? afterSubscribed = null)
    {
        var subscriptionName = await Store.Subscriptions.CreateAsync(new SubscriptionCreationOptions { Query = query });
        var delivered = new ConcurrentBag<string>();

        using var worker = Store.Subscriptions.GetSubscriptionWorker<Widget>(
            new SubscriptionWorkerOptions(subscriptionName)
            {
                TimeToWaitBeforeConnectionRetry = TimeSpan.FromMilliseconds(200),
            });

        using var cts = new CancellationTokenSource();
        var run = worker.Run(
            batch =>
            {
                foreach (var item in batch.Items)
                {
                    delivered.Add(item.Id);
                }

                return Task.CompletedTask;
            },
            cts.Token);

        try
        {
            if (afterSubscribed is not null)
            {
                await afterSubscribed();
            }

            await AsyncWait.UntilAsync(
                () => until([.. delivered]),
                description,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await run;
            }
            catch (Exception)
            {
                // Cancelling is the only way to stop the worker loop and it aborts an in-flight TCP
                // read, so the task ends in whatever that produced (OperationCanceled, IO, Socket).
                // Awaiting only observes it; the shutdown path is not under test.
            }
        }

        return [.. delivered];
    }

    [Fact]
    public async Task now_is_rejected_outright_in_a_subscription_query()
    {
        // The behaviour that broke the RavenDB upgrade. It is a server-side validation error raised
        // when the worker connects — not a query that quietly returns nothing — so the fix is to
        // stop asking the question, never to pin RavenDB back.
        await SeedAsync(session => session.StoreAsync(new Widget { Status = "Pending" }));

        var act = async () => await DeliveredByAsync(
            "from Widgets where Status = 'Pending' and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())",
            d => d.Count > 0,
            "a now()-gated subscription to deliver anything");

        var thrown = await act.Should().ThrowAsync<RavenException>(
            "RavenDB 7.2.5+ validates subscription expressions and refuses now()");

        thrown.Which.Message.Should().Contain("now()").And.Contain("not supported",
            "the server names the unsupported function — if this message changes, #258's diagnosis "
            + "needs rechecking rather than the assertion loosening");
        thrown.Which.Message.Should().Contain("subscription",
            "the restriction is specific to filter/subscription expressions; now() is still fine in "
            + "an ordinary query, which is evaluated afresh each time it runs");
    }

    [Fact]
    public async Task A_subscription_only_reevaluates_a_document_when_it_is_written()
    {
        // The root cause behind both #258 and #233, shown directly. The document does not match when
        // it is created; no amount of elapsed time changes that; a write is what brings it back.
        var parked = new Widget { Status = "Pending", NextAttemptAtUtc = DateTime.UtcNow.AddHours(-1), WakeUp = false };
        var sentinel = new Widget { Status = "Pending", NextAttemptAtUtc = null };

        await SeedAsync(async session =>
        {
            await session.StoreAsync(parked);
            await session.StoreAsync(sentinel);
        });

        // Stored before the sentinel, so etag order means the sentinel's arrival proves `parked` was
        // already offered to the query and rejected. A negative asserted on an observed signal
        // rather than on a sleep.
        var delivered = await DeliveredByAsync(
            "from Widgets where Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true)",
            d => d.Contains(sentinel.Id!),
            "the gated subscription to deliver the new (ungated) document");

        delivered.Should().NotContain(parked.Id!,
            "its backoff elapsed an hour ago, but WakeUp is false and nothing has written to it — "
            + "the elapsed time is invisible to the subscription");

        // Now write to it. This is exactly what SyncActionRetrySweeper does, and the only reason a
        // parked action is ever seen again.
        var woken = await DeliveredByAsync(
            "from Widgets where Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true)",
            d => d.Contains(parked.Id!),
            "the parked document to be delivered once a write makes the gate true",
            afterSubscribed: async () =>
            {
                using var session = Store.OpenAsyncSession();
                session.Advanced.Patch<Widget, bool>(parked.Id!, w => w.WakeUp, true);
                await session.SaveChangesAsync();
            });

        woken.Should().Contain(parked.Id!,
            "setting the gate is simultaneously the write that triggers re-evaluation — one patch "
            + "does both jobs, which is what makes the sweeper pattern work");
    }

    [Fact]
    public async Task A_boolean_gate_expresses_the_backoff_that_now_could_not()
    {
        // The replacement, end to end: new actions flow immediately, parked ones stay parked, and a
        // future-dated action is not woken just because the sweeper ran.
        var brandNew = new Widget { Status = "Pending", NextAttemptAtUtc = null };
        var parkedDue = new Widget { Status = "Pending", NextAttemptAtUtc = DateTime.UtcNow.AddHours(-1), WakeUp = true };
        var parkedNotDue = new Widget { Status = "Pending", NextAttemptAtUtc = DateTime.UtcNow.AddHours(1), WakeUp = false };
        var finished = new Widget { Status = "Completed", NextAttemptAtUtc = null };

        await SeedAsync(async session =>
        {
            await session.StoreAsync(parkedNotDue);
            await session.StoreAsync(finished);
            await session.StoreAsync(parkedDue);
            await session.StoreAsync(brandNew);
        });

        // brandNew has the highest etag, so its delivery means every other document has been offered.
        var delivered = await DeliveredByAsync(
            "from Widgets where Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true)",
            d => d.Contains(brandNew.Id!),
            "the gated subscription to deliver the newest document");

        delivered.Should().Contain(brandNew.Id!, "a new action has no backoff to wait for");
        delivered.Should().Contain(parkedDue.Id!, "the sweeper has declared this one due");
        delivered.Should().NotContain(parkedNotDue.Id!, "its backoff has not elapsed, so WakeUp is false");
        delivered.Should().NotContain(finished.Id!, "it is no longer Pending");
    }
}
