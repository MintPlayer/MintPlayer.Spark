using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Consumer: a queued write-back's delivery (warehouse §4.2.5). Red on e0c542f9: any 2xx (207 included) was recorded
/// Completed (<c>Workers/SyncActionSubscriptionWorker.cs:75-85</c>, F12), and retries ended after about 5–8 minutes
/// (F14). Replaces <c>SyncActionSubscriptionWorkerE2ETests</c> and <c>SyncActionRetrySweeperTests</c>.
/// </summary>
public class SyncActionDispatchTests : SparkTestDriver
{
    private const string DocumentId = "ReplSourceCars/1-A";
    private const string ActionId = "SparkSyncActions/op-1";

    private readonly ISyncActionSender _sender = Substitute.For<ISyncActionSender>();
    private readonly ISyncOutcomeNotifier _notifier = Substitute.For<ISyncOutcomeNotifier>();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using var session = Store.OpenAsyncSession();
        var now = _clock.GetUtcNow().UtcDateTime;
        await session.StoreAsync(new SparkSyncAction
        {
            Id = ActionId,
            OpId = "op-1",
            OwnerModule = "Fleet",
            ActionType = SyncActionType.Update,
            SourceCollection = "ReplSourceCars",
            TargetCollection = "ReplicaCars",
            DocumentId = DocumentId,
            Changes = [new SparkSyncActionChange { Property = "LicensePlate", BaseJson = "\"OLD-1\"", ValueJson = "\"NEW-1\"" }],
            InitiatorId = "SparkUsers/alice",
            CreatedAtUtc = now,
            GiveUpAtUtc = now.AddHours(48),
        }, ActionId);
        await session.StoreAsync(new SyncPendingMarker
        {
            Id = SyncPendingMarker.For(DocumentId),
            DocumentId = DocumentId,
            OwnerModule = "Fleet",
            SyncActionId = ActionId,
            CreatedAtUtc = now,
        }, SyncPendingMarker.For(DocumentId));
        await session.SaveChangesAsync();
    }

    private SyncActionDispatchRecipient Recipient()
        => new(Store, _sender, _notifier, Microsoft.Extensions.Options.Options.Create(ReplicationFixtures.Options("HR")), _clock,
            NullLogger<SyncActionDispatchRecipient>.Instance);

    private void Owner(HttpStatusCode? status, SyncApplyResult? result = null)
        => _sender.SendAsync(Arg.Any<string>(), Arg.Any<SyncApplyRequest>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ModuleCallResult<SyncApplyResult>(status, result, status is null ? "unreachable" : "")));

    private Task Dispatch() => Recipient().HandleAsync(new SyncActionDispatchMessage { SyncActionId = ActionId });

    private async Task<(SparkSyncAction Action, SyncPendingMarker? Marker)> ReadAsync()
    {
        using var session = Store.OpenAsyncSession();
        return ((await session.LoadAsync<SparkSyncAction>(ActionId))!, await session.LoadAsync<SyncPendingMarker>(SyncPendingMarker.For(DocumentId)));
    }

    [Fact]
    public async Task Applied_is_Completed_and_the_row_stays_pending_until_the_delivery()
    {
        Owner(HttpStatusCode.OK, new SyncApplyResult { Outcome = SyncApplyOutcome.Applied });

        await Dispatch();

        var (action, marker) = await ReadAsync();
        action.Status.Should().Be(SyncActionStatuses.Completed);
        marker.Should().NotBeNull();
        marker!.SyncActionId.Should().BeNull();
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);
    }

    /// <summary>The queued request is the stored one: same op id (F15), the stored bases, the initiator.</summary>
    [Fact]
    public async Task The_stored_edit_is_sent_with_its_op_id_and_bases()
    {
        Owner(HttpStatusCode.OK, new SyncApplyResult { Outcome = SyncApplyOutcome.Applied });

        await Dispatch();

        await _sender.Received(1).SendAsync("Fleet", Arg.Is<SyncApplyRequest>(r =>
            r.OpId == "op-1" && r.InitiatorId == "SparkUsers/alice" && r.Changes.Single().Base.GetString() == "OLD-1"),
            null, Arg.Any<CancellationToken>());
    }

    /// <summary>F12 red→green: a conflict reaches the initiator.</summary>
    [Fact]
    public async Task A_conflict_is_recorded_the_pending_mark_removed_and_the_initiator_told()
    {
        Owner(HttpStatusCode.Conflict, new SyncApplyResult { Outcome = SyncApplyOutcome.Conflict, Reason = "changed", Attributes = ["LicensePlate"] });

        await Dispatch();

        var (action, marker) = await ReadAsync();
        action.Status.Should().Be(SyncActionStatuses.Conflict);
        action.Attributes.Should().BeEquivalentTo(["LicensePlate"]);
        marker.Should().BeNull();
        await _notifier.Received(1).NotifyAsync(Arg.Is<SparkSyncAction>(a => a.Id == ActionId && a.InitiatorId == "SparkUsers/alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_is_Refused_and_told()
    {
        Owner(HttpStatusCode.Forbidden, new SyncApplyResult { Outcome = SyncApplyOutcome.Refused, Refusal = SyncRefusalKind.Forbidden, Reason = "may not write LicensePlate" });

        await Dispatch();

        (await ReadAsync()).Action.Status.Should().Be(SyncActionStatuses.Refused);
        await _notifier.Received(1).NotifyAsync(Arg.Any<SparkSyncAction>(), Arg.Any<CancellationToken>());
    }

    /// <summary>F14: an unreachable owner is retried on the ladder, well past the old 5–8 minutes.</summary>
    [Fact]
    public async Task An_unreachable_owner_is_retried_on_the_ladder()
    {
        Owner(null);
        _clock.Advance(TimeSpan.FromHours(3));

        var act = Dispatch;

        (await act.Should().ThrowAsync<RetryAfterException>()).Which.Delay.Should().Be(TimeSpan.FromHours(2));
        var (action, marker) = await ReadAsync();
        action.Status.Should().Be(SyncActionStatuses.Pending);
        action.Attempts.Should().Be(1);
        marker.Should().NotBeNull();
    }

    /// <summary>Past the retry budget (48 h) it gives up: the initiator is told (and mailed by the notifier).</summary>
    [Fact]
    public async Task Past_the_budget_it_gives_up_and_tells_the_initiator()
    {
        Owner(null);
        _clock.Advance(TimeSpan.FromHours(49));

        await Dispatch();

        var (action, marker) = await ReadAsync();
        action.Status.Should().Be(SyncActionStatuses.GaveUp);
        marker.Should().BeNull();
        await _notifier.Received(1).NotifyAsync(Arg.Is<SparkSyncAction>(a => a.Status == SyncActionStatuses.GaveUp), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_action_that_is_no_longer_pending_is_not_sent()
    {
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<SparkSyncAction>(ActionId))!.Status = SyncActionStatuses.Completed;
            await session.SaveChangesAsync();
        }

        await Dispatch();

        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default, default);
    }
}
