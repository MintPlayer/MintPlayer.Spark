using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// <see cref="MessageRetrySweeper"/> takes its ids from <see cref="SparkMessages_ByQueue"/>, which under
/// load lags the documents by seconds. Each test indexes a message, stops the index, then moves the
/// document on, so the index lists it in a state it has left: exactly what a loaded host sees. The
/// sweep must judge the document, not the index entry.
/// <para>
/// Found through S-M3 failing under a loaded sweep (contributions_PRD §5c): a completed message was
/// rewritten by every sweep until the index caught up, 11 times in one run.
/// </para>
/// </summary>
public class MessageRetrySweeperStaleIndexTests : SparkTestDriver
{
    protected override IEnumerable<System.Reflection.Assembly> IndexAssemblies
        => [typeof(SparkMessages_ByQueue).Assembly];

    private MessageRetrySweeper NewSweeper()
        => new(
            Store,
            Options.Create(new SparkMessagingOptions()),
            NullLogger<MessageRetrySweeper>.Instance,
            new MessagingLeaseManager(Store, NullLogger<MessagingLeaseManager>.Instance) { IsHeld = true });

    private static SparkMessage Message(EMessageStatus status) => new()
    {
        QueueName = "stale-index-queue",
        MessageType = "Irrelevant, Irrelevant",
        PayloadJson = "{}",
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
        MaxAttempts = 5,
        Status = status,
    };

    /// <summary>Stores <paramref name="message"/>, waits until the index has it, then stops the index.</summary>
    private async Task<string> IndexThenFreezeAsync(SparkMessage message)
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(message);
            await session.SaveChangesAsync();
        }
        await Store.WaitForIndexingAsync();
        await Store.Maintenance.SendAsync(new StopIndexOperation(new SparkMessages_ByQueue().IndexName));
        return message.Id!;
    }

    private async Task<(SparkMessage Message, string ChangeVector)> ChangeAsync(string id, Action<SparkMessage> change)
    {
        using var session = Store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(id);
        change(message);
        await session.SaveChangesAsync();
        return (message, session.Advanced.GetChangeVectorFor(message)!);
    }

    private async Task<(SparkMessage Message, string ChangeVector)> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(id);
        return (message, session.Advanced.GetChangeVectorFor(message)!);
    }

    [Fact]
    public async Task Wake_up_leaves_alone_a_message_that_completed_since_the_index_saw_it_deferred()
    {
        var deferred = Message(EMessageStatus.Pending);
        deferred.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-5);
        var id = await IndexThenFreezeAsync(deferred);

        var (_, before) = await ChangeAsync(id, m =>
        {
            m.Status = EMessageStatus.Completed;
            m.CompletedAtUtc = DateTime.UtcNow;
        });

        (await NewSweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0, "the message is no longer due: it completed");

        var (after, changeVector) = await LoadAsync(id);
        changeVector.Should().Be(before, "a completed message must not be written to at all");
        after.WakeUp.Should().BeFalse();
    }

    [Fact]
    public async Task Wake_up_does_not_rewrite_a_message_already_woken_since_the_index_saw_it()
    {
        var deferred = Message(EMessageStatus.Pending);
        deferred.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-5);
        var id = await IndexThenFreezeAsync(deferred);

        var sweeper = NewSweeper();
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1, "the first sweep wakes the due message");
        var (woken, before) = await LoadAsync(id);
        woken.WakeUp.Should().BeTrue();

        // The index still says WakeUp is false.
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "it is woken already");
        (await LoadAsync(id)).ChangeVector.Should().Be(before);
    }

    [Fact]
    public async Task Wake_up_does_not_skip_a_new_backoff_the_index_has_not_seen()
    {
        var failed = Message(EMessageStatus.Failed);
        failed.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-5);
        var id = await IndexThenFreezeAsync(failed);

        // Picked up, failed again, and parked for a later retry.
        var (_, before) = await ChangeAsync(id, m =>
        {
            m.AttemptCount = 2;
            m.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(10);
        });

        (await NewSweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0, "its new backoff has not elapsed");
        var (after, changeVector) = await LoadAsync(id);
        changeVector.Should().Be(before);
        after.WakeUp.Should().BeFalse("waking it now would retry it ten minutes early");
    }

    [Fact]
    public async Task Wake_up_still_wakes_a_message_that_is_due()
    {
        var deferred = Message(EMessageStatus.Pending);
        deferred.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-5);
        var id = await IndexThenFreezeAsync(deferred);

        (await NewSweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        var (after, _) = await LoadAsync(id);
        after.WakeUp.Should().BeTrue();
        after.LastWakeUpUtc.Should().HaveValue();
        after.Status.Should().Be(EMessageStatus.Pending);
    }

    [Fact]
    public async Task Reclaim_leaves_alone_a_message_that_completed_since_the_index_saw_its_claim_lapse()
    {
        var abandoned = Message(EMessageStatus.Processing);
        abandoned.OwnerId = "a-host/1";
        abandoned.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        abandoned.AttemptCount = 1;
        var id = await IndexThenFreezeAsync(abandoned);

        var (_, before) = await ChangeAsync(id, m =>
        {
            m.Status = EMessageStatus.Completed;
            m.OwnerId = null;
            m.ClaimExpiresAtUtc = null;
            m.CompletedAtUtc = DateTime.UtcNow;
        });

        (await NewSweeper().ReclaimAbandonedAsync(CancellationToken.None)).Should().Be(0);

        var (after, changeVector) = await LoadAsync(id);
        changeVector.Should().Be(before);
        after.Status.Should().Be(EMessageStatus.Completed, "reclaiming it would run its handlers a second time");
        after.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Reclaim_leaves_alone_a_claim_renewed_since_the_index_saw_it_lapse()
    {
        var abandoned = Message(EMessageStatus.Processing);
        abandoned.OwnerId = "a-host/1";
        abandoned.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        abandoned.AttemptCount = 1;
        var id = await IndexThenFreezeAsync(abandoned);

        var (_, before) = await ChangeAsync(id, m => m.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(5));

        (await NewSweeper().ReclaimAbandonedAsync(CancellationToken.None)).Should().Be(0);
        var (after, changeVector) = await LoadAsync(id);
        changeVector.Should().Be(before);
        after.Status.Should().Be(EMessageStatus.Processing);
        after.OwnerId.Should().Be("a-host/1");
    }

    [Fact]
    public async Task Reclaim_still_reclaims_a_lapsed_claim()
    {
        var abandoned = Message(EMessageStatus.Processing);
        abandoned.OwnerId = "a-host/1";
        abandoned.ClaimExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        abandoned.AttemptCount = 1;
        var id = await IndexThenFreezeAsync(abandoned);

        (await NewSweeper().ReclaimAbandonedAsync(CancellationToken.None)).Should().Be(1);
        var (after, _) = await LoadAsync(id);
        after.Status.Should().Be(EMessageStatus.Pending);
        after.OwnerId.Should().BeNull();
        after.ClaimExpiresAtUtc.Should().NotHaveValue();
        after.WakeUp.Should().BeTrue();
        after.AttemptCount.Should().Be(2);
    }
}
